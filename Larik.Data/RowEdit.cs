using System.Text.RegularExpressions;

namespace Larik.Data;

/// <summary>The table and column a result-set column came from. Built by the host's resolver.</summary>
public readonly record struct ColumnOrigin(string? Schema, string Table, string Column);

/// <summary>One part of the row identity: a primary-key column and where to read it in the result.</summary>
public sealed record RowEditKeyColumn(int ResultIndex, string Column, string? DataType);

/// <summary>A table column a result-set column writes to.</summary>
public sealed record RowEditColumn(string Column, string? DataType);

/// <summary>
/// One table whose rows a result can edit. A join can produce several of these: every table whose
/// complete primary key is in the result becomes its own target, so editing a joined row is as safe
/// as editing a single-table one.
/// </summary>
public sealed class RowEditTarget
{
    public string? Schema { get; init; }

    public required string Table { get; init; }

    /// <summary>Primary-key columns in key order, each with the result column that holds its value.</summary>
    public required IReadOnlyList<RowEditKeyColumn> Key { get; init; }

    /// <summary>Writable columns, keyed by the result column index.</summary>
    public required IReadOnlyDictionary<int, RowEditColumn> Columns { get; init; }

    public string Label => string.IsNullOrWhiteSpace(Schema) ? Table : $"{Schema}.{Table}";
}

/// <summary>The tables a result can edit, or the reason none can.</summary>
public sealed class RowEditResolution
{
    public static RowEditResolution None(string refusal, IReadOnlyDictionary<int, string>? reasons = null) => new()
    {
        Targets = [],
        Refusal = refusal,
        Reasons = reasons ?? new Dictionary<int, string>(),
    };

    public required IReadOnlyList<RowEditTarget> Targets { get; init; }

    /// <summary>Empty when at least one target exists; otherwise what to tell the user.</summary>
    public required string Refusal { get; init; }

    /// <summary>Per-result-column reasons for mapped columns whose table did not qualify.</summary>
    public IReadOnlyDictionary<int, string> Reasons { get; init; } = new Dictionary<int, string>();
}

/// <summary>
/// Decides whether a result set can be edited and, if so, which table each writable column belongs
/// to. The bar is deliberately high: a row must be provably identifiable by its primary key and the
/// produced columns must be plain references, because an inline save writes exactly one row.
/// <para>
/// This is the same conservative stance as the foreign-key resolver: when the SQL shape makes the
/// source ambiguous the grid stays read-only and says why, rather than writing somewhere plausible.
/// </para>
/// </summary>
public static class RowEditResolver
{
    private const string NoSource =
        "Editing needs a plain SELECT from a table whose rows the result still identifies.";

    private const string ShapeRefusal =
        "Editing is off for grouped, distinct, combined or computed queries. Run a plain SELECT and edit that.";

    // A SELECT whose shape changes the row identity, so a primary key match no longer proves one row.
    private static readonly string[] ShapeWords =
    [
        "GROUP", "HAVING", "DISTINCT", "UNION", "INTERSECT", "EXCEPT",
        "PIVOT", "UNPIVOT", "APPLY", "INTO", "OVER",
    ];

    private static readonly string[] AggregateWords =
    [
        "COUNT", "SUM", "AVG", "MIN", "MAX", "STRING_AGG", "GROUPING", "STDEV", "VAR",
        "ARRAY_AGG", "JSON_AGG", "JSONB_AGG", "BOOL_AND", "BOOL_OR",
    ];

    // A column of these types can be a key, but the grid shows it without enough precision to build
    // a WHERE that is guaranteed to match the same row.
    private static readonly string[] LossyKeyTypes =
    [
        "datetime", "datetime2", "datetimeoffset", "time",
    ];

    /// <summary>
    /// Resolves the editable tables. <paramref name="origins"/> maps a result column index to the
    /// table column the host's lexical resolver found for it; unmapped columns are simply absent.
    /// </summary>
    public static RowEditResolution Resolve(
        QueryResult result,
        string? statement,
        IReadOnlyList<TableSchema> tables,
        IReadOnlyDictionary<int, ColumnOrigin> origins)
    {
        if (result.Columns.Count == 0)
        {
            return RowEditResolution.None("This result has no columns to edit.");
        }

        if (string.IsNullOrWhiteSpace(statement) || origins.Count == 0)
        {
            return RowEditResolution.None(NoSource);
        }

        var text = statement.Trim();
        while (text.EndsWith(';'))
        {
            text = text[..^1].TrimEnd();
        }

        // A batch is scoped to the whole text, so its columns cannot be attributed safely.
        if (text.Length == 0 || text.Contains(';') || HasNonEditableShape(text))
        {
            return RowEditResolution.None(ShapeRefusal);
        }

        // Group mapped columns by the table they came from.
        var groups = new Dictionary<string, List<(int Index, ColumnOrigin Origin)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in origins)
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Table))
            {
                continue;
            }

            var key = $"{(pair.Value.Schema ?? string.Empty).Trim()}\u001F{pair.Value.Table.Trim()}";
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add((pair.Key, pair.Value));
        }

        var targets = new List<RowEditTarget>();
        var reasons = new List<string>();
        var reasonsByColumn = new Dictionary<int, string>();

        foreach (var group in groups.Values)
        {
            var schema = group[0].Origin.Schema ?? string.Empty;
            var tableName = group[0].Origin.Table;

            var schemaInfo = tables.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, tableName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Schema ?? string.Empty, schema, StringComparison.OrdinalIgnoreCase));

            // Every column of this table gets the same one-line reason, so the status bar can explain
            // a joined column ("BizEntity's key isn't in the result") instead of a generic refusal.
            void Reject(string reason)
            {
                reasons.Add(reason);
                foreach (var item in group)
                {
                    reasonsByColumn.TryAdd(item.Index, reason);
                }
            }

            if (schemaInfo is null)
            {
                Reject($"The schema doesn't know {Qualify(schema, tableName)} yet.");
                continue;
            }

            if (schemaInfo.Kind != TableKind.Table)
            {
                Reject($"{schemaInfo.Name} is a view, so it has no row to update.");
                continue;
            }

            if (schemaInfo.PrimaryKey.Count == 0)
            {
                Reject($"{schemaInfo.Name} has no primary key, so a row can't be identified.");
                continue;
            }

            var key = new List<RowEditKeyColumn>(schemaInfo.PrimaryKey.Count);
            var missing = new List<string>();
            var ambiguous = false;

            foreach (var keyColumn in schemaInfo.PrimaryKey)
            {
                var matches = group
                    .Where(item => string.Equals(item.Origin.Column, keyColumn, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Index)
                    .Distinct()
                    .ToList();

                if (matches.Count == 1)
                {
                    key.Add(new RowEditKeyColumn(
                        matches[0],
                        keyColumn,
                        TypeOf(schemaInfo, keyColumn) ?? result.Columns[matches[0]].DataType));
                }
                else if (matches.Count > 1)
                {
                    ambiguous = true;
                }
                else
                {
                    missing.Add(keyColumn);
                }
            }

            if (ambiguous)
            {
                Reject($"The result shows the key of {schemaInfo.Name} more than once, so a row can't be identified.");
                continue;
            }

            if (missing.Count > 0)
            {
                Reject(
                    $"The result doesn't include the primary key of {schemaInfo.Name} ({string.Join(", ", missing)}).");
                continue;
            }

            if (key.FirstOrDefault(column => IsLossyKeyType(column.DataType)) is { } lossy)
            {
                Reject(
                    $"{schemaInfo.Name}.{lossy.Column} is a date/time key shown without enough precision to match one row.");
                continue;
            }

            var columns = new Dictionary<int, RowEditColumn>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (index, origin) in group)
            {
                var detail = FindColumn(schemaInfo, origin.Column);
                var dataType = detail?.DataType ?? result.Columns[index].DataType;

                // An identity or computed column cannot be written; a value that appears twice cannot
                // be attributed to one column confidently, so it stays read-only.
                if (detail is { IsIdentity: true } or { IsComputed: true })
                {
                    reasonsByColumn.TryAdd(
                        index,
                        $"{schemaInfo.Name}.{origin.Column} is generated and can't be written.");
                    continue;
                }

                if (IsGeneratedType(dataType))
                {
                    reasonsByColumn.TryAdd(
                        index,
                        $"{schemaInfo.Name}.{origin.Column} is a row version and can't be written.");
                    continue;
                }

                if (!used.Add(origin.Column))
                {
                    continue;
                }

                columns[index] = new RowEditColumn(origin.Column, dataType);
            }

            if (columns.Count == 0)
            {
                Reject($"Every column of {schemaInfo.Name} in this result is generated or read-only.");
                continue;
            }

            targets.Add(new RowEditTarget
            {
                Schema = string.IsNullOrWhiteSpace(schemaInfo.Schema) ? null : schemaInfo.Schema,
                Table = schemaInfo.Name,
                Key = key,
                Columns = columns,
            });
        }

        return targets.Count > 0
            ? new RowEditResolution
            {
                Targets = targets,
                Refusal = string.Empty,
                Reasons = reasonsByColumn,
            }
            : RowEditResolution.None(reasons.FirstOrDefault() ?? NoSource, reasonsByColumn);
    }

    private static TableColumn? FindColumn(TableSchema table, string column) =>
        table.ColumnDetails.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, column, StringComparison.OrdinalIgnoreCase));

    private static string? TypeOf(TableSchema table, string column) => FindColumn(table, column)?.DataType;

    private static bool IsGeneratedType(string? dataType)
    {
        var type = (dataType ?? string.Empty).Split('(', 2)[0].Trim();
        return type.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
            || type.Equals("rowversion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLossyKeyType(string? dataType)
    {
        var type = (dataType ?? string.Empty).Split('(', 2)[0].Trim();
        return LossyKeyTypes.Contains(type, StringComparer.OrdinalIgnoreCase);
    }

    private static string Qualify(string schema, string table) =>
        string.IsNullOrWhiteSpace(schema) ? table : $"{schema}.{table}";

    // ---------------------------------------------------------------- statement shape

    /// <summary>
    /// True when the statement's shape means a primary-key match no longer identifies one row, or
    /// when its select list contains an expression whose display value is not the column's value.
    /// Lexical and conservative: a keyword inside a string or comment is ignored, anything else
    /// refuses the result.
    /// </summary>
    private static bool HasNonEditableShape(string text)
    {
        foreach (var word in ShapeWords)
        {
            if (FindWord(text, word, 0, text.Length) >= 0)
            {
                return true;
            }
        }

        // FROM (SELECT …) / JOIN (SELECT …): the derived column names may alias expressions, so the
        // displayed value need not be the table column's value.
        if (HasDerivedTable(text))
        {
            return true;
        }

        foreach (var word in AggregateWords)
        {
            var index = FindWord(text, word, 0, text.Length);
            while (index >= 0)
            {
                var next = index + word.Length;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                if (next < text.Length && text[next] == '(')
                {
                    return true;
                }

                index = FindWord(text, word, next, text.Length);
            }
        }

        var select = FindWord(text, "SELECT", 0, text.Length);
        if (select < 0)
        {
            return true;
        }

        var from = FindWord(text, "FROM", select + 6, text.Length);
        if (from < 0)
        {
            return true;
        }

        var list = text[(select + 6)..from];
        list = TopPrefix.Replace(list, string.Empty);

        // Every projected item must be a plain column reference, a star, or one of those with an
        // alias. An expression would show a value that is not the column's value.
        return !list
            .Split(',')
            .All(IsSimpleProjection);
    }

    private static readonly Regex SimpleProjection = new(
        @"^\s*(?:\*|(?:[A-Za-z_@#$][A-Za-z0-9_@#$]*|x)(?:\.(?:[A-Za-z_@#$][A-Za-z0-9_@#$]*|x|\*))*(?:\s+(?:AS\s+)?(?:[A-Za-z_@#$][A-Za-z0-9_@#$]*|x))?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex QuotedIdentifier = new(
        "\\[[^\\]]*\\]|\"[^\"]*\"",
        RegexOptions.Singleline);

    private static readonly Regex TopPrefix = new(
        @"^\s*TOP\s*(?:\(\s*\d+\s*\)|\d+)\s*(?:PERCENT\s*)?(?:WITH\s+TIES\s*)?",
        RegexOptions.IgnoreCase);

    private static readonly string[] TableWords = ["FROM", "JOIN"];

    /// <summary>True when a FROM or JOIN is immediately followed by a parenthesised query.</summary>
    private static bool HasDerivedTable(string text)
    {
        foreach (var word in TableWords)
        {
            var index = FindWord(text, word, 0, text.Length);
            while (index >= 0)
            {
                var next = index + word.Length;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                if (next < text.Length && text[next] == '(')
                {
                    return true;
                }

                index = FindWord(text, word, next, text.Length);
            }
        }

        return false;
    }

    private static bool IsSimpleProjection(string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return false;
        }

        // A quoted identifier may contain dots or spaces; collapse each to a neutral token first.
        var collapsed = QuotedIdentifier.Replace(item, "x");
        return SimpleProjection.IsMatch(collapsed);
    }

    /// <summary>
    /// Finds <paramref name="word"/> as a standalone token outside strings, quoted identifiers and
    /// comments, at parenthesis depth zero.
    /// </summary>
    private static int FindWord(string text, string word, int start, int end)
    {
        var depth = 0;
        for (var index = Math.Max(0, start); index < end; index++)
        {
            var current = text[index];
            switch (current)
            {
                case '\'':
                case '"':
                    index = SkipQuoted(text, index, current, end);
                    continue;
                case '[':
                    index = SkipBracket(text, index, end);
                    continue;
                case '-' when index + 1 < end && text[index + 1] == '-':
                    index = SkipLineComment(text, index, end);
                    continue;
                case '/' when index + 1 < end && text[index + 1] == '*':
                    index = SkipBlockComment(text, index, end);
                    continue;
                case '(':
                    depth++;
                    continue;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    continue;
            }

            if (depth != 0 || !char.IsLetter(current))
            {
                continue;
            }

            if (index > 0 && IsIdentifierChar(text[index - 1]))
            {
                continue;
            }

            if (index + word.Length > end
                || !text.AsSpan(index, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var after = index + word.Length;
            if (after == end || !IsIdentifierChar(text[after]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsIdentifierChar(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '@' or '#' or '$';

    private static int SkipQuoted(string text, int index, char quote, int end)
    {
        // A doubled quote is an escaped quote, not the end of the literal.
        for (var cursor = index + 1; cursor < end; cursor++)
        {
            if (text[cursor] != quote)
            {
                continue;
            }

            if (cursor + 1 < end && text[cursor + 1] == quote)
            {
                cursor++;
                continue;
            }

            return cursor;
        }

        return end - 1;
    }

    private static int SkipBracket(string text, int index, int end)
    {
        for (var cursor = index + 1; cursor < end; cursor++)
        {
            if (text[cursor] != ']')
            {
                continue;
            }

            if (cursor + 1 < end && text[cursor + 1] == ']')
            {
                cursor++;
                continue;
            }

            return cursor;
        }

        return end - 1;
    }

    private static int SkipLineComment(string text, int index, int end)
    {
        var newline = text.IndexOf('\n', index);
        return newline < 0 || newline >= end ? end - 1 : newline;
    }

    private static int SkipBlockComment(string text, int index, int end)
    {
        var close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
        return close < 0 || close >= end ? end - 1 : close + 1;
    }
}

