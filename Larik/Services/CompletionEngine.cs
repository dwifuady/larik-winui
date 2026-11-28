using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Larik.Data;
using Larik.Models;

namespace Larik.Services;

/// <summary>What the caret is positioned to type, derived from the closest keyword.</summary>
public enum CompletionScope
{
    /// <summary>No schema position: snippets, keywords and everything else.</summary>
    Keyword,

    /// <summary>After FROM, JOIN, INTO, UPDATE, ...: table names.</summary>
    Table,

    /// <summary>After SELECT, WHERE, ON, ...: columns.</summary>
    Column,

    /// <summary>After USE: database names.</summary>
    Database,

    /// <summary>After EXEC/EXECUTE/CALL: stored procedure names, expanded with their parameters.</summary>
    Routine,
}

/// <summary>The table an UPDATE/INSERT writes, when the caret is in its column list.</summary>
public sealed record StatementTarget(TableSchema Table, bool Assignment);

/// <summary>A table named by FROM/JOIN in the statement under the caret.</summary>
public sealed record TableReference(TableSchema? Table, string Name, string? Alias);

/// <summary>
/// One SELECT-list item that is a plain column reference: the qualifier as written (<c>s</c>,
/// <c>dbo.Settings</c> or nothing), the source column and the name the result column receives.
/// </summary>
public sealed record SelectProjection(string? Qualifier, string Column, string OutputName);

/// <summary>Everything the engine needs to answer one query, resolved by the completion source.</summary>
public sealed record CompletionRequest(
    string Word,
    string? Qualifier,
    CompletionScope Scope,
    string? Keyword,
    IReadOnlyList<SnippetDef> Snippets,
    IReadOnlyList<TableSchema> Tables,
    IReadOnlyList<TableReference> References,
    IReadOnlyList<TableSchema> Ctes,
    IReadOnlyList<string> Variables,
    IReadOnlyList<string> Databases,
    StatementTarget? Target,
    string Clipboard)
{
    /// <summary>
    /// The active SQL dialect's completion metadata. Null keeps the historical T-SQL lists, so the
    /// engine behaves exactly as before for providers without an editor language.
    /// </summary>
    public ISqlDialect? Dialect { get; init; }

    /// <summary>Procedures the active database exposes; empty when the provider has none.</summary>
    public IReadOnlyList<RoutineSchema> Routines { get; init; } = [];
}

/// <summary>
/// Snippet + keyword + schema completion, modelled on the desktop app's Monaco provider
/// (snippets match on trigger prefix, keywords and schema objects fill the rest, tables gain
/// aliases and joins are filled from the columns the statement already references).
/// </summary>
public static partial class CompletionEngine
{
    public static IReadOnlyList<string> Keywords { get; } =
    [
        "SELECT", "FROM", "JOIN", "WHERE", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "ON",
        "GROUP BY", "ORDER BY", "HAVING", "TOP", "DISTINCT", "AS", "AND", "OR", "NOT", "NULL", "IS NULL",
        "IN", "EXISTS", "BETWEEN", "LIKE", "CASE WHEN", "ELSE", "END", "UNION", "UNION ALL", "INSERT INTO",
        "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "TRUNCATE TABLE",
        "BEGIN TRANSACTION", "COMMIT", "ROLLBACK", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "ROW_NUMBER",
        "OVER", "PARTITION BY", "CAST", "GETDATE", "SYSDATETIME", "DATEADD", "DATEDIFF", "FORMAT",
    ];

    /// <summary>Built-in functions offered in column positions, inserted as <c>NAME()</c>.</summary>
    public static IReadOnlyList<string> Functions { get; } =
    [
        "ABS", "AVG", "CAST", "CEILING", "CHARINDEX", "COALESCE", "CONCAT", "CONVERT", "COUNT",
        "CUME_DIST", "CURRENT_TIMESTAMP", "DATEADD", "DATEDIFF", "DATENAME", "DATEPART", "DAY",
        "DENSE_RANK", "EOMONTH", "FLOOR", "FORMAT", "GETDATE", "GETUTCDATE", "IIF", "ISDATE",
        "ISNULL", "ISNUMERIC", "LAG", "LEAD", "LEN", "LOWER", "LTRIM", "MAX", "MIN", "MONTH",
        "NEWID", "NTILE", "NULLIF", "OBJECT_ID", "OBJECT_NAME", "PARSE", "PERCENT_RANK", "POWER",
        "RAND", "RANK", "REPLACE", "REVERSE", "ROUND", "ROW_NUMBER", "RTRIM", "SCOPE_IDENTITY",
        "SIGN", "SQRT", "STRING_AGG", "STUFF", "SUBSTRING", "SUM", "SYSDATETIME", "SYSDATETIMEOFFSET",
        "TRIM", "TRY_CAST", "TRY_CONVERT", "UPPER", "YEAR",
    ];

    /// <summary>Words after which only a table name makes sense.</summary>
    private static readonly FrozenSet<string> TableContext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "OUTER JOIN",
        "CROSS JOIN", "APPLY", "CROSS APPLY", "OUTER APPLY", "INTO", "UPDATE", "TABLE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Table positions where a generated alias is part of the suggestion.</summary>
    private static readonly FrozenSet<string> AliasContext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "OUTER JOIN",
        "CROSS JOIN", "APPLY", "CROSS APPLY", "OUTER APPLY",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Words after which column names make sense.</summary>
    private static readonly FrozenSet<string> ColumnContext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WHERE", "ON", "AND", "OR", "SET", "HAVING", "ORDER BY", "GROUP BY", "BY",
        "DISTINCT", "LIKE", "IN", "BETWEEN", "IS", "NOT", "THEN", "WHEN",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Words that follow a table reference but are not aliases.</summary>
    private static readonly FrozenSet<string> NotAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "ON", "GROUP",
        "ORDER", "HAVING", "SET", "UNION", "EXCEPT", "INTERSECT", "AS", "WITH", "SELECT",
        "FROM", "AND", "OR", "APPLY", "TABLESAMPLE", "PIVOT", "UNPIVOT", "FOR", "OPTION",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dialect table-context words, cached per dialect because completion runs while typing.</summary>
    private static readonly ConditionalWeakTable<ISqlDialect, FrozenSet<string>> DialectTableContext = new();

    /// <summary>The historical routine words, reproduced when a provider exposes no dialect.</summary>
    private static readonly FrozenSet<string> RoutineContext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "EXEC", "EXECUTE", "CALL",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly ConditionalWeakTable<ISqlDialect, FrozenSet<string>> DialectRoutineContext = new();

    private static FrozenSet<string> TableContextFor(ISqlDialect? dialect) =>
        dialect is null
            ? TableContext
            : DialectTableContext.GetValue(
                dialect,
                static value => value.TableContextKeywords.ToFrozenSet(StringComparer.OrdinalIgnoreCase));

    private static FrozenSet<string> RoutineContextFor(ISqlDialect? dialect) =>
        dialect is null
            ? RoutineContext
            : DialectRoutineContext.GetValue(
                dialect,
                static value => value.RoutineContextKeywords.ToFrozenSet(StringComparer.OrdinalIgnoreCase));

    private const int KeywordLimit = 8;
    private const int SchemaLimit = 500;
    private const int JoinLimit = 8;

    /// <summary>The word being typed, plus the qualifier when the user typed <c>alias.</c>.</summary>
    public static (int Start, string Word, string? Qualifier) ExtractContext(string text, int caret)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, string.Empty, null);
        }

        caret = Math.Clamp(caret, 0, text.Length);

        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        var word = text[start..caret];
        string? qualifier = null;

        if (start > 0 && text[start - 1] == '.')
        {
            // The qualifier may be bare (t) or quoted the way the dialect writes it ([dbo], "dbo").
            var dot = start - 1;
            var qStart = dot;
            var closer = qStart > 0 ? text[qStart - 1] : '\0';

            if (closer is ']' or '"')
            {
                var opener = closer == ']' ? '[' : '"';
                qStart--;
                while (qStart > 0 && text[qStart - 1] != opener)
                {
                    qStart--;
                }

                if (qStart > 0)
                {
                    qStart--;
                }
            }
            else
            {
                while (qStart > 0 && IsWordChar(text[qStart - 1]))
                {
                    qStart--;
                }
            }

            if (qStart < dot)
            {
                qualifier = NormalizeIdentifier(text[qStart..dot]);
            }
        }

        return (start, word, qualifier);
    }

    /// <summary>
    /// What the caret is positioned to type, and which keyword put it there. Only the statement
    /// under the caret counts, and the word being typed is ignored so "FROM cust" still reads as
    /// a table position.
    /// </summary>
    public static (CompletionScope Scope, string? Keyword) DetectScope(
        string text,
        int caret,
        ISqlDialect? dialect = null)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var tableContext = TableContextFor(dialect);
        var routineContext = RoutineContextFor(dialect);

        var wordStart = caret;
        while (wordStart > 0 && IsWordChar(text[wordStart - 1]))
        {
            wordStart--;
        }

        var start = StatementStart(text, caret);
        var beforeEnd = Math.Max(start, Math.Min(wordStart, caret));
        var before = text.AsSpan(start, beforeEnd - start).TrimEnd();
        if (before.Length == 0)
        {
            return (CompletionScope.Keyword, null);
        }

        // "USE [name" is still typing an identifier; drop the bracket before reading words.
        if (before[^1] == '[')
        {
            before = before[..^1].TrimEnd();
        }

        // Operators and separators are followed by a column, not by a keyword.
        if (before.Length > 0 && before[^1] is '=' or '<' or '>' or '!' or ',' or '(')
        {
            return (CompletionScope.Column, null);
        }

        // Read only the trailing two whitespace-delimited tokens instead of splitting the whole statement.
        var lastEnd = before.Length;
        var lastStart = lastEnd;
        while (lastStart > 0 && !char.IsWhiteSpace(before[lastStart - 1]))
        {
            lastStart--;
        }

        var lastRaw = before.Slice(lastStart, lastEnd - lastStart);
        if (lastRaw.Length == 0)
        {
            return (CompletionScope.Keyword, null);
        }

        var prevEnd = lastStart;
        while (prevEnd > 0 && char.IsWhiteSpace(before[prevEnd - 1]))
        {
            prevEnd--;
        }

        var hasPrev = prevEnd > 0;
        var prevStart = prevEnd;
        while (prevStart > 0 && !char.IsWhiteSpace(before[prevStart - 1]))
        {
            prevStart--;
        }

        var prevRaw = before.Slice(prevStart, prevEnd - prevStart);

        // USE <database> at the start of a batch.
        if (!hasPrev && lastRaw.Equals("USE", StringComparison.OrdinalIgnoreCase))
        {
            return (CompletionScope.Database, "USE");
        }

        var routineLookup = routineContext.GetAlternateLookup<ReadOnlySpan<char>>();
        var tableLookup = tableContext.GetAlternateLookup<ReadOnlySpan<char>>();
        var columnLookup = ColumnContext.GetAlternateLookup<ReadOnlySpan<char>>();

        // "FROM dbo.orders", "FROM dbo." and "WHERE a.b" all carry a qualifier in the last token,
        // so the clause is decided by the word before it.
        if (hasPrev && lastRaw.Contains('.'))
        {
            if (routineLookup.TryGetValue(prevRaw, out var rPrev))
            {
                return (CompletionScope.Routine, rPrev);
            }

            if (tableLookup.TryGetValue(prevRaw, out var tPrev))
            {
                return (CompletionScope.Table, tPrev);
            }

            if (columnLookup.TryGetValue(prevRaw, out var cPrev))
            {
                return (CompletionScope.Column, cPrev);
            }
        }

        var lastTrimmed = lastRaw.TrimEnd('.');
        if (hasPrev && prevRaw.Length + 1 + lastTrimmed.Length <= 64)
        {
            Span<char> combinedBuf = stackalloc char[prevRaw.Length + 1 + lastTrimmed.Length];
            prevRaw.CopyTo(combinedBuf);
            combinedBuf[prevRaw.Length] = ' ';
            lastTrimmed.CopyTo(combinedBuf.Slice(prevRaw.Length + 1));
            ReadOnlySpan<char> combinedSpan = combinedBuf;

            if (routineLookup.TryGetValue(combinedSpan, out var rComb))
            {
                return (CompletionScope.Routine, rComb);
            }

            if (routineLookup.TryGetValue(lastTrimmed, out var rLast))
            {
                return (CompletionScope.Routine, rLast);
            }

            if (tableLookup.TryGetValue(combinedSpan, out var tComb))
            {
                return (CompletionScope.Table, tComb);
            }

            if (tableLookup.TryGetValue(lastTrimmed, out var tLast))
            {
                return (CompletionScope.Table, tLast);
            }

            if (columnLookup.TryGetValue(combinedSpan, out var cComb))
            {
                return (CompletionScope.Column, cComb);
            }

            if (columnLookup.TryGetValue(lastTrimmed, out var cLast))
            {
                return (CompletionScope.Column, cLast);
            }

            return (CompletionScope.Keyword, null);
        }

        if (routineLookup.TryGetValue(lastTrimmed, out var rSingle))
        {
            return (CompletionScope.Routine, rSingle);
        }

        if (tableLookup.TryGetValue(lastTrimmed, out var tSingle))
        {
            return (CompletionScope.Table, tSingle);
        }

        if (columnLookup.TryGetValue(lastTrimmed, out var cSingle))
        {
            return (CompletionScope.Column, cSingle);
        }

        return (CompletionScope.Keyword, null);
    }

    /// <summary>FROM/JOIN table references in the statement under the caret, in query order.</summary>
    public static IReadOnlyList<TableReference> FindTableReferences(
        string text,
        int caret,
        IReadOnlyList<TableSchema> tables) =>
        FindTableReferences(text, caret, tables, []);

    /// <summary>
    /// Top-level FROM/JOIN references (subquery and CTE bodies are skipped); <paramref name="ctes"/>
    /// shadow real tables, as they do in SQL.
    /// </summary>
    public static IReadOnlyList<TableReference> FindTableReferences(
        string text,
        int caret,
        IReadOnlyList<TableSchema> tables,
        IReadOnlyList<TableSchema> ctes)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = StatementStart(text, caret);
        if (caret <= start)
        {
            return [];
        }

        var statement = text[start..StatementEnd(text, caret)];
        var references = new List<TableReference>();
        var depth = 0;
        var depthCursor = 0;
        foreach (Match match in TableReferencePattern().Matches(statement))
        {
            while (depthCursor < match.Index)
            {
                var ch = statement[depthCursor++];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    if (depth > 0) depth--;
                }
            }

            if (depth != 0)
            {
                continue;
            }

            var (schema, name) = SplitQualified(match.Groups["table"].Value);
            var table = FindTable(name, schema, ctes) ?? FindTable(name, schema, tables);

            string? alias = null;
            if (match.Groups["alias"].Success)
            {
                var candidate = NormalizeIdentifier(match.Groups["alias"].Value);
                if (!NotAliases.Contains(candidate) &&
                    !string.Equals(candidate, table?.Name ?? name, StringComparison.OrdinalIgnoreCase))
                {
                    alias = candidate;
                }
            }

            references.Add(new TableReference(table, name, alias));
        }

        return references;
    }

    /// <summary>
    /// CTEs defined by the statement under the caret, with their columns: an explicit
    /// <c>cte (a, b) AS</c> list, the SELECT aliases of the body, or the source table when the
    /// body selects <c>*</c> from one table.
    /// </summary>
    public static IReadOnlyList<TableSchema> FindCtes(string text, int caret, IReadOnlyList<TableSchema> tables)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var statement = text[StatementStart(text, caret)..caret];

        var with = WithClausePattern().Match(statement);
        if (!with.Success)
        {
            return [];
        }

        var ctes = new List<TableSchema>();
        var index = with.Index + with.Length;

        while (true)
        {
            while (index < statement.Length && char.IsWhiteSpace(statement[index]))
            {
                index++;
            }

            var nameStart = index;
            while (index < statement.Length && (char.IsLetterOrDigit(statement[index]) || statement[index] is '_' or '[' or ']' or '$' or '#' or '@'))
            {
                index++;
            }

            var name = NormalizeIdentifier(statement[nameStart..index]);
            if (name.Length == 0)
            {
                break;
            }

            while (index < statement.Length && char.IsWhiteSpace(statement[index]))
            {
                index++;
            }

            var explicitColumns = new List<string>();
            if (index < statement.Length && statement[index] == '(')
            {
                var close = statement.IndexOf(')', index);
                if (close < 0)
                {
                    break;
                }

                explicitColumns = statement[(index + 1)..close]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeIdentifier)
                    .Where(column => column.Length > 0)
                    .ToList();
                index = close + 1;

                while (index < statement.Length && char.IsWhiteSpace(statement[index]))
                {
                    index++;
                }
            }

            if (index + 1 >= statement.Length ||
                string.Compare(statement, index, "AS", 0, 2, StringComparison.OrdinalIgnoreCase) != 0)
            {
                break;
            }

            index += 2;
            while (index < statement.Length && char.IsWhiteSpace(statement[index]))
            {
                index++;
            }

            if (index >= statement.Length || statement[index] != '(')
            {
                break;
            }

            var bodyStart = index + 1;
            var depth = 1;
            index = bodyStart;
            while (index < statement.Length && depth > 0)
            {
                if (statement[index] == '(')
                {
                    depth++;
                }
                else if (statement[index] == ')')
                {
                    depth--;
                }

                index++;
            }

            if (depth != 0)
            {
                // The body is still being typed; earlier CTEs are already usable.
                break;
            }

            ctes.Add(BuildCte(name, explicitColumns, statement[bodyStart..(index - 1)], tables));

            while (index < statement.Length && char.IsWhiteSpace(statement[index]))
            {
                index++;
            }

            if (index >= statement.Length || statement[index] != ',')
            {
                break;
            }

            index++;
        }

        return ctes;
    }

    /// <summary>
    /// The table an UPDATE or INSERT writes, when the caret is in its column list
    /// (<c>INSERT INTO x (…)</c> or <c>UPDATE x SET …</c>). <see cref="StatementTarget.Assignment"/>
    /// asks for <c>column = </c> insertion.
    /// </summary>
    public static StatementTarget? FindTarget(string text, int caret, IReadOnlyList<TableSchema> tables)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var statement = text[StatementStart(text, caret)..caret];

        var insert = InsertStatementPattern().Match(statement);
        if (insert.Success)
        {
            var (insertSchema, insertName) = SplitQualified(insert.Groups["table"].Value);
            var table = FindTable(insertName, insertSchema, tables);
            if (table is null)
            {
                return null;
            }

            var afterTable = statement[(insert.Index + insert.Length)..];
            var open = afterTable.IndexOf('(');

            // Only the first parenthesis group directly after the table is the column list;
            // "VALUES (" or an already closed list is not.
            if (open < 0 || !string.IsNullOrWhiteSpace(afterTable[..open]) || afterTable[(open + 1)..].Contains(')'))
            {
                return null;
            }

            return new StatementTarget(table, Assignment: false);
        }

        var update = UpdateStatementPattern().Match(statement);
        if (!update.Success)
        {
            return null;
        }

        var (updateSchema, updateName) = SplitQualified(update.Groups["table"].Value);
        var updated = FindTable(updateName, updateSchema, tables);
        if (updated is null)
        {
            return null;
        }

        var afterUpdate = statement[(update.Groups["table"].Index + update.Groups["table"].Length)..];
        var set = SetKeywordPattern().Match(afterUpdate);
        if (!set.Success)
        {
            return null;
        }

        var afterSet = afterUpdate[(set.Index + set.Length)..];
        return new StatementTarget(updated, Assignment: !WhereKeywordPattern().IsMatch(afterSet));
    }

    /// <summary>T-SQL variables declared before the caret (<c>DECLARE @x INT</c>).</summary>
    public static IReadOnlyList<string> DeclaredVariables(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match declaration in DeclarePattern().Matches(text[..caret]))
        {
            foreach (Match variable in VariablePattern().Matches(declaration.Groups["body"].Value))
            {
                if (seen.Add(variable.Value))
                {
                    found.Add(variable.Value);
                }
            }
        }

        return found;
    }

    public static IReadOnlyList<CompletionItem> Suggest(CompletionRequest request)
    {
        var needle = request.Word;
        var results = new List<CompletionItem>();

        if (request.Qualifier is not null)
        {
            // EXEC dbo. lists that schema's procedures; the qualifier is already typed.
            if (request.Scope == CompletionScope.Routine)
            {
                AddRoutines(results, request, needle);
                return Finish(results, int.MaxValue);
            }

            // dbo. in a table position lists that schema's tables; the qualifier is already typed,
            // so the insert carries only the table name.
            if (request.Scope == CompletionScope.Table && MatchedSchema(request) is not null)
            {
                AddTables(results, request, needle);
                return Finish(results, int.MaxValue);
            }

            // alias. or table. -> that table's columns, whatever the clause.
            var table = ResolveQualifier(request.References, request.Qualifier, request.Tables, request.Ctes);
            IReadOnlyList<TableSchema> sources;
            if (table is not null)
            {
                sources = [table];
            }
            else
            {
                var mentioned = new List<TableSchema>();
                for (var i = 0; i < request.References.Count; i++)
                {
                    if (request.References[i].Table is { } refTable && !mentioned.Contains(refTable))
                    {
                        mentioned.Add(refTable);
                    }
                }

                sources = mentioned.Count > 0 ? mentioned : request.Tables;
            }

            AddColumns(results, sources, needle, request.Dialect);
            return Finish(results, SchemaLimit);
        }

        switch (request.Scope)
        {
            case CompletionScope.Database:
                AddDatabases(results, request.Databases, needle);
                break;

            case CompletionScope.Routine:
                AddRoutines(results, request, needle);
                break;

            case CompletionScope.Table:
                if (MatchedSchema(request) is null)
                {
                    AddCtes(results, request.Ctes, needle);
                }

                AddTables(results, request, needle);
                break;

            case CompletionScope.Column:
                if (request.Target is { } target)
                {
                    AddTargetColumns(results, target, needle, request.Dialect);
                    break;
                }

                AddVariables(results, request.Variables, needle);

                // Join conditions belong to the ON clause. After WHERE they would paper over the
                // predicate the user is actually writing.
                if (string.Equals(request.Keyword, "ON", StringComparison.OrdinalIgnoreCase))
                {
                    AddJoinConditions(results, request, needle);
                }

                AddScopedColumns(results, request, needle);
                AddCteColumns(results, request, needle);
                AddContinuationClauses(results, request, needle);
                AddFunctions(results, needle, request.Dialect?.Functions);
                break;

            default:
                if (request.Word.Length == 0)
                {
                    return [];
                }

                AddSnippets(results, request.Snippets, request.Word, request.Clipboard);
                AddVariables(results, request.Variables, needle);
                AddKeywords(results, needle, request.Dialect?.Keywords);
                AddPlainTables(results, request, needle);
                AddColumns(results, request.Tables, needle, request.Dialect);
                break;
        }

        // A table or database list is what these clauses are for: show them all, the popup scrolls.
        var max = request.Scope switch
        {
            CompletionScope.Table or CompletionScope.Database or CompletionScope.Routine => int.MaxValue,
            CompletionScope.Keyword => KeywordLimit,
            _ => SchemaLimit,
        };

        return Finish(results, max);
    }

    /// <summary>
    /// Turns snippet markup into insertable text:
    /// ${cursor} -> final caret, ${n:default} -> prefilled text (the first marker is left selected
    /// so typing replaces it; markers are independent defaults, not linked tab stops),
    /// ${paste} -> clipboard.
    /// </summary>
    public static (string Text, int Caret, (int Start, int Length)? Selection) Expand(string content, string clipboard)
    {
        var builder = new StringBuilder();
        var markers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        (int Start, int Length)? selection = null;
        var caret = -1;

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '$' && i + 1 < content.Length && content[i + 1] == '{')
            {
                var close = content.IndexOf('}', i + 2);
                if (close > i)
                {
                    var token = content[(i + 2)..close];
                    i = close;

                    if (token.Equals("cursor", StringComparison.OrdinalIgnoreCase))
                    {
                        caret = builder.Length;
                        continue;
                    }

                    if (token.Equals("paste", StringComparison.OrdinalIgnoreCase))
                    {
                        builder.Append(clipboard);
                        continue;
                    }

                    var colon = token.IndexOf(':');
                    var key = colon >= 0 ? token[..colon] : token;
                    var value = colon >= 0 ? token[(colon + 1)..] : string.Empty;

                    if (key.Length > 0 && key.All(char.IsDigit))
                    {
                        // Reusing a number keeps the first value for older/custom snippets, but the
                        // editor does not link the copies after insertion.
                        if (markers.TryGetValue(key, out var existing))
                        {
                            builder.Append(existing);
                        }
                        else
                        {
                            markers[key] = value;
                            var at = builder.Length;
                            builder.Append(value);
                            selection ??= (at, value.Length);
                        }

                        continue;
                    }
                }
            }

            builder.Append(content[i]);
        }

        var text = builder.ToString();
        var caretAt = caret >= 0
            ? caret
            : selection is { } sel
                ? sel.Start + sel.Length
                : text.Length;

        return (text, caretAt, selection);
    }

    /// <summary>
    /// True when a snippet needs the clipboard. Suggestion building is synchronous, so a snippet
    /// using <c>${paste}</c> is expanded again by the editor when it is accepted.
    /// </summary>
    public static bool UsesClipboard(string content) =>
        content.Contains("${paste}", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- candidates

    private static void AddSnippets(List<CompletionItem> results, IReadOnlyList<SnippetDef> snippets, string word, string clipboard)
    {
        foreach (var snippet in snippets)
        {
            var triggerMatch = snippet.Trigger.StartsWith(word, StringComparison.OrdinalIgnoreCase);
            if (!triggerMatch && !snippet.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expanded = Expand(snippet.Content, clipboard);
            results.Add(new CompletionItem
            {
                Label = snippet.Trigger,
                Detail = snippet.Name,
                Kind = CompletionKind.Snippet,
                InsertText = expanded.Text,
                CaretOffset = expanded.Caret,
                Selection = expanded.Selection,
                PasteTemplate = UsesClipboard(snippet.Content) ? snippet.Content : null,
                RankSort = triggerMatch ? 0 : 1,

                // A trigger prefix is what the user is typing; a name substring is a search
                // fallback and must not outrank a keyword/table that actually starts with it.
                MatchRank = triggerMatch ? 0 : 1,
            });
        }
    }

    private static void AddKeywords(
        List<CompletionItem> results,
        string needle,
        IReadOnlyList<string>? keywords)
    {
        var source = keywords ?? Keywords;
        for (var index = 0; index < source.Count; index++)
        {
            var keyword = source[index];
            if (!Matches(keyword, needle))
            {
                continue;
            }

            results.Add(new CompletionItem
            {
                Label = keyword,
                Detail = "keyword",
                Kind = CompletionKind.Keyword,
                InsertText = keyword,

                // The dialect declares its keywords in reading order (SELECT, FROM, WHERE, ...);
                // keeping that order beats sorting matches alphabetically.
                RankSort = index,
            });
        }
    }

    /// <summary>
    /// Clauses that can follow a predicate. Offered while writing one (after WHERE/ON/HAVING) and
    /// ranked below columns, so ORDER BY / GROUP BY are reachable without pushing columns down.
    /// </summary>
    private static readonly HashSet<string> ContinuationClauses = new(StringComparer.OrdinalIgnoreCase)
    {
        "GROUP BY", "HAVING", "ORDER BY", "UNION", "UNION ALL", "OPTION",
    };

    private static void AddContinuationClauses(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        var source = request.Dialect?.Keywords ?? Keywords;
        for (var index = 0; index < source.Count; index++)
        {
            var keyword = source[index];
            if (!ContinuationClauses.Contains(keyword) || !Matches(keyword, needle))
            {
                continue;
            }

            results.Add(new CompletionItem
            {
                Label = keyword,
                Detail = "clause",
                Kind = CompletionKind.Keyword,
                InsertText = keyword,
                RankSort = index,
            });
        }
    }

    private static void AddFunctions(
        List<CompletionItem> results,
        string needle,
        IReadOnlyList<string>? functions)
    {
        var source = functions ?? Functions;
        for (var i = 0; i < source.Count; i++)
        {
            var function = source[i];
            if (!Matches(function, needle))
            {
                continue;
            }

            var insert = $"{function}()";
            results.Add(new CompletionItem
            {
                Label = insert,
                Detail = "built-in function",
                Kind = CompletionKind.Function,
                InsertText = insert,
                CaretOffset = insert.Length - 1,
            });
        }
    }

    /// <summary>
    /// Tables in a table position. In FROM/JOIN they carry a generated alias, and the name is
    /// schema-qualified and quoted the way the active dialect requires, so the insert runs as
    /// written (<c>SELECT * FROM [dbo].[Orders] AS [o]</c>). A schema typed before the dot
    /// (<c>FROM dbo.</c>) filters the list and is not repeated in the insert.
    /// </summary>
    private static void AddTables(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        var typedSchema = MatchedSchema(request);
        var dialect = request.Dialect;
        var tables = request.Tables;

        if (request.Keyword is null || !AliasContext.Contains(request.Keyword))
        {
            results.EnsureCapacity(results.Count + tables.Count);
            for (var i = 0; i < tables.Count; i++)
            {
                var table = tables[i];
                if (typedSchema is not null && !string.Equals(table.Schema, typedSchema, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Matches(table.Name, needle))
                {
                    continue;
                }

                results.Add(new CompletionItem
                {
                    Label = table.Name,
                    Detail = $"{SchemaPrefix(table)}{ObjectKindLabel(table)} · {table.Columns.Count} columns",
                    Kind = CompletionKind.Table,
                    InsertText = TableNameFor(table, dialect, typedSchema),
                });
            }

            return;
        }

        HashSet<string>? used = null;
        for (var i = 0; i < request.References.Count; i++)
        {
            if (request.References[i].Alias is { } existingAlias)
            {
                used ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                used.Add(existingAlias);
            }
        }

        results.EnsureCapacity(results.Count + (tables.Count * 2));
        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (typedSchema is not null && !string.Equals(table.Schema, typedSchema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Matches(table.Name, needle))
            {
                continue;
            }

            var alias = GenerateUniqueAlias(table.Name, used);
            var aliasText = dialect is null ? alias : dialect.QuoteIdentifier(alias);
            var qualifiedName = TableNameFor(table, dialect, typedSchema);
            var schemaPrefix = SchemaPrefix(table);
            var kindLabel = ObjectKindLabel(table);

            results.Add(new CompletionItem
            {
                Label = table.Name,
                Detail = $"{schemaPrefix}{kindLabel} · AS {aliasText}",
                Kind = CompletionKind.Table,
                InsertText = $"{qualifiedName} AS {aliasText}",
                RankSort = 0,
            });

            results.Add(new CompletionItem
            {
                Label = $"{table.Name} (no alias)",
                Detail = $"{schemaPrefix}{kindLabel} · no alias",
                Kind = CompletionKind.Table,
                InsertText = qualifiedName,
                RankSort = 1,
            });
        }
    }

    private static void AddPlainTables(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        var dialect = request.Dialect;
        var tables = request.Tables;
        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (!Matches(table.Name, needle))
            {
                continue;
            }

            results.Add(new CompletionItem
            {
                Label = table.Name,
                Detail = $"{SchemaPrefix(table)}{ObjectKindLabel(table)} · {table.Columns.Count} columns",
                Kind = CompletionKind.Table,
                InsertText = TableNameFor(table, dialect, typedSchema: null),
            });
        }
    }

    /// <summary>
    /// Procedures in an EXEC/EXECUTE/CALL position. Accepting one expands the call with every
    /// parameter set to NULL and selects the first value, so the user types over it instead of
    /// remembering the signature. The call text itself belongs to the dialect.
    /// </summary>
    private static void AddRoutines(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        var typedSchema = MatchedRoutineSchema(request);
        var dialect = request.Dialect;
        var routines = request.Routines;

        for (var i = 0; i < routines.Count; i++)
        {
            var routine = routines[i];
            if (routine.Kind != RoutineKind.Procedure)
            {
                continue;
            }

            if (typedSchema is not null && !string.Equals(routine.Schema, typedSchema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Matches(routine.Name, needle))
            {
                continue;
            }

            var call = dialect is null
                ? new RoutineCallText(routine.Name)
                : dialect.RoutineCall(typedSchema is null ? routine.Schema : null, routine.Name, routine.Parameters);

            results.Add(new CompletionItem
            {
                Label = routine.Name,
                Detail = $"{SchemaPrefix(routine.Schema)}proc · {ParameterSummary(routine.Parameters)}",
                Kind = CompletionKind.Routine,
                InsertText = call.Text,
                Selection = call.HasSelection ? (call.SelectionStart, call.SelectionLength) : null,
                RankSort = 0,
            });
        }
    }

    private static string ParameterSummary(IReadOnlyList<RoutineParameter> parameters) => parameters.Count switch
    {
        0 => "no params",
        1 => "1 param",
        _ => $"{parameters.Count} params",
    };

    /// <summary>The schema typed before the dot in a table position, when it names a real schema.</summary>
    private static string? MatchedSchema(CompletionRequest request)
    {
        if (request.Qualifier is not { } qualifier)
        {
            return null;
        }

        var tables = request.Tables;
        for (var i = 0; i < tables.Count; i++)
        {
            if (string.Equals(tables[i].Schema, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return qualifier;
            }
        }

        return null;
    }

    /// <summary>The schema typed before the dot in a routine position.</summary>
    private static string? MatchedRoutineSchema(CompletionRequest request)
    {
        if (request.Qualifier is not { } qualifier)
        {
            return null;
        }

        var routines = request.Routines;
        for (var i = 0; i < routines.Count; i++)
        {
            if (string.Equals(routines[i].Schema, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return qualifier;
            }
        }

        return null;
    }

    private static string SchemaPrefix(TableSchema table) => SchemaPrefix(table.Schema);

    private static string ObjectKindLabel(TableSchema table) =>
        table.Kind == TableKind.View ? "view" : "table";

    private static string SchemaPrefix(string? schema) =>
        string.IsNullOrWhiteSpace(schema) ? string.Empty : $"{schema} · ";

    /// <summary><c>[dbo].[Orders]</c>, or just <c>[Orders]</c> when the schema was already typed.</summary>
    private static string TableNameFor(TableSchema table, ISqlDialect? dialect, string? typedSchema)
    {
        var name = dialect is null ? table.Name : dialect.QuoteIdentifier(table.Name);
        if (typedSchema is not null || string.IsNullOrWhiteSpace(table.Schema))
        {
            return name;
        }

        var schema = table.Schema!;
        return dialect is null ? $"{schema}.{name}" : $"{dialect.QuoteIdentifier(schema)}.{name}";
    }

    private static void AddCtes(List<CompletionItem> results, IReadOnlyList<TableSchema> ctes, string needle)
    {
        for (var i = 0; i < ctes.Count; i++)
        {
            var cte = ctes[i];
            if (!Matches(cte.Name, needle))
            {
                continue;
            }

            results.Add(new CompletionItem
            {
                Label = cte.Name,
                Detail = $"CTE · {cte.Columns.Count} columns",
                Kind = CompletionKind.Cte,
                InsertText = cte.Name,
                RankSort = 0,
            });
        }
    }

    /// <summary>Columns of CTEs that the statement has not joined yet (e.g. while typing SELECT).</summary>
    private static void AddCteColumns(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        if (request.Ctes.Count == 0)
        {
            return;
        }

        var dialect = request.Dialect;
        HashSet<string>? referenced = null;
        for (var i = 0; i < request.References.Count; i++)
        {
            if (request.References[i].Table is { } refTable)
            {
                referenced ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                referenced.Add(refTable.Name);
            }
        }

        for (var i = 0; i < request.Ctes.Count; i++)
        {
            var cte = request.Ctes[i];
            if (referenced is not null && referenced.Contains(cte.Name))
            {
                continue;
            }

            var columns = cte.Columns;
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                if (!Matches(column, needle))
                {
                    continue;
                }

                results.Add(new CompletionItem
                {
                    Label = column,
                    Detail = $"CTE · {cte.Name}.{column}",
                    Kind = CompletionKind.Column,
                    InsertText = Quote(dialect, column),
                });
            }
        }
    }

    /// <summary>Columns of the statement's tables; aliased tables are inserted as <c>alias.column</c>.</summary>
    private static void AddScopedColumns(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        var dialect = request.Dialect;

        if (request.References.Count == 0)
        {
            AddColumns(results, request.Tables, needle, dialect);
            return;
        }

        var seenRefs = new HashSet<string>(request.References.Count, StringComparer.OrdinalIgnoreCase);
        for (var r = 0; r < request.References.Count; r++)
        {
            var reference = request.References[r];
            if (!seenRefs.Add(reference.Alias ?? reference.Name) || reference.Table is not { } table)
            {
                continue;
            }

            var alias = reference.Alias;
            var quotedAlias = string.IsNullOrEmpty(alias) ? null : Quote(dialect, alias);
            var columns = table.Columns;
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                string? label = null;
                if (!Matches(column, needle))
                {
                    if (alias is null)
                    {
                        continue;
                    }

                    label = $"{alias}.{column}";
                    if (!Matches(label, needle))
                    {
                        continue;
                    }
                }

                label ??= alias is null ? column : $"{alias}.{column}";
                results.Add(new CompletionItem
                {
                    Label = label,
                    Detail = $"{table.Name}.{column}",
                    Kind = CompletionKind.Column,
                    InsertText = quotedAlias is null ? Quote(dialect, column) : $"{quotedAlias}.{Quote(dialect, column)}",
                });
            }
        }
    }

    private static void AddColumns(
        List<CompletionItem> results,
        IReadOnlyList<TableSchema> tables,
        string needle,
        ISqlDialect? dialect)
    {
        for (var t = 0; t < tables.Count; t++)
        {
            var table = tables[t];
            var columns = table.Columns;
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                if (!Matches(column, needle))
                {
                    continue;
                }

                results.Add(new CompletionItem
                {
                    Label = column,
                    Detail = $"{table.Name}.{column}",
                    Kind = CompletionKind.Column,
                    InsertText = Quote(dialect, column),
                });
            }
        }
    }

    /// <summary>Columns of the table an UPDATE/INSERT writes; SET positions ask for <c>column = </c>.</summary>
    private static void AddTargetColumns(
        List<CompletionItem> results,
        StatementTarget target,
        string needle,
        ISqlDialect? dialect)
    {
        var columns = target.Table.Columns;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (!Matches(column, needle))
            {
                continue;
            }

            var insert = Quote(dialect, column);
            results.Add(new CompletionItem
            {
                Label = column,
                Detail = $"{target.Table.Name}.{column}",
                Kind = CompletionKind.Column,
                InsertText = target.Assignment ? $"{insert} = " : insert,
            });
        }
    }

    private static string Quote(ISqlDialect? dialect, string name) =>
        dialect is null ? name : dialect.QuoteIdentifier(name);

    /// <summary><c>alias.column</c> with both parts quoted the way the dialect requires.</summary>
    private static string ColumnInsert(string? qualifier, string column, ISqlDialect? dialect) =>
        string.IsNullOrEmpty(qualifier)
            ? Quote(dialect, column)
            : $"{Quote(dialect, qualifier)}.{Quote(dialect, column)}";

    private static void AddVariables(List<CompletionItem> results, IReadOnlyList<string> variables, string needle)
    {
        for (var i = 0; i < variables.Count; i++)
        {
            var variable = variables[i];
            // The label carries the '@'; match what the user types after it too.
            if (!Matches(variable, needle) && !Matches(variable.AsSpan().TrimStart('@'), needle))
            {
                continue;
            }

            results.Add(new CompletionItem
            {
                Label = variable,
                Detail = "local variable",
                Kind = CompletionKind.Variable,
                InsertText = variable,
            });
        }
    }

    private static void AddDatabases(List<CompletionItem> results, IReadOnlyList<string> databases, string needle)
    {
        for (var i = 0; i < databases.Count; i++)
        {
            var database = databases[i];
            if (!Matches(database, needle))
            {
                continue;
            }

            results.Add(new CompletionItem
            {
                Label = database,
                Detail = "database",
                Kind = CompletionKind.Database,
                InsertText = database,
            });
        }
    }

    /// <summary>
    /// Join conditions for the table named last in the statement, matched against the tables
    /// before it. Declared foreign keys win; when the provider has none, the desktop app's name
    /// heuristics fill in (<c>customer_id</c> -> <c>customers.id</c>, shared columns, reverse).
    /// </summary>
    private static void AddJoinConditions(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        var references = request.References.Where(reference => reference.Table is not null).ToList();
        if (references.Count < 2)
        {
            return;
        }

        var joined = references[^1];
        var joinedTable = joined.Table!;
        var joinedAlias = joined.Alias ?? joinedTable.Name;

        var prior = references
            .Take(references.Count - 1)
            .DistinctBy(reference => reference.Alias ?? reference.Name, StringComparer.OrdinalIgnoreCase)
            .Reverse()
            .ToList();

        var suggestions = new List<JoinSuggestion>();
        var dialect = request.Dialect;

        // Join text is a field expression like any other: alias and column carry the dialect's
        // quoting so reserved-word columns survive (<c>[o].[Key] = [s].[Key]</c>).
        string Qualified(string alias, string column) => ColumnInsert(alias, column, dialect);

        var joinedNormalized = new string[joinedTable.Columns.Count];
        for (var i = 0; i < joinedTable.Columns.Count; i++)
        {
            joinedNormalized[i] = NormalizeName(joinedTable.Columns[i]);
        }

        var joinedKeys = PreferredKeyColumns(joinedTable);

        foreach (var other in prior)
        {
            var otherTable = other.Table!;
            var otherAlias = other.Alias ?? otherTable.Name;

            // Declared foreign keys: joined.customer_id -> other.id (and the reverse direction).
            foreach (var group in joinedTable.ForeignKeys
                         .Where(key => ForeignKeyTargets(key, otherTable))
                         .GroupBy(key => key.Constraint, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                suggestions.Add(new JoinSuggestion(
                    $"Join on {first.Column} = {first.ReferencedColumn}",
                    "Foreign key",
                    string.Join(" AND ", group.Select(key => $"{Qualified(joinedAlias, key.Column)} = {Qualified(otherAlias, key.ReferencedColumn)}")),
                    1000 - (group.Count() - 1)));
            }

            foreach (var group in otherTable.ForeignKeys
                         .Where(key => ForeignKeyTargets(key, joinedTable))
                         .GroupBy(key => key.Constraint, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                suggestions.Add(new JoinSuggestion(
                    $"Join on {first.ReferencedColumn} = {first.Column}",
                    "Foreign key (reverse)",
                    string.Join(" AND ", group.Select(key => $"{Qualified(joinedAlias, key.ReferencedColumn)} = {Qualified(otherAlias, key.Column)}")),
                    980 - (group.Count() - 1)));
            }

            // joined.customer_id -> other.id
            var otherKeys = PreferredKeyColumns(otherTable);
            for (var c = 0; c < joinedTable.Columns.Count; c++)
            {
                var column = joinedTable.Columns[c];
                var stem = ColumnStemFromNormalized(joinedNormalized[c]);
                if (stem is null || otherKeys.Count == 0 || !MatchesTableStem(stem, otherTable.Name))
                {
                    continue;
                }

                for (var k = 0; k < otherKeys.Count; k++)
                {
                    var key = otherKeys[k];
                    suggestions.Add(new JoinSuggestion(
                        $"Join on {column} = {key}",
                        "Heuristic match",
                        $"{Qualified(joinedAlias, column)} = {Qualified(otherAlias, key)}",
                        900));
                }
            }

            var otherNormalized = new string[otherTable.Columns.Count];
            for (var i = 0; i < otherTable.Columns.Count; i++)
            {
                otherNormalized[i] = NormalizeName(otherTable.Columns[i]);
            }

            // Columns with the same name on both sides (id = id, status = status, ...).
            for (var c = 0; c < joinedTable.Columns.Count; c++)
            {
                var normCol = joinedNormalized[c];
                for (var o = 0; o < otherTable.Columns.Count; o++)
                {
                    if (!normCol.Equals(otherNormalized[o], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var column = joinedTable.Columns[c];
                    var otherColumn = otherTable.Columns[o];
                    var isKey = ContainsIgnoreCase(joinedTable.PrimaryKey, column)
                                || ContainsIgnoreCase(otherTable.PrimaryKey, otherColumn);

                    suggestions.Add(new JoinSuggestion(
                        $"Join on {column} = {otherColumn}",
                        "Heuristic match",
                        $"{Qualified(joinedAlias, column)} = {Qualified(otherAlias, otherColumn)}",
                        isKey ? 760 : 720));
                }
            }

            // other.customer_id -> joined.id (the reverse direction).
            for (var o = 0; o < otherTable.Columns.Count; o++)
            {
                var column = otherTable.Columns[o];
                var stem = ColumnStemFromNormalized(otherNormalized[o]);
                if (stem is null || joinedKeys.Count == 0 || !MatchesTableStem(stem, joinedTable.Name))
                {
                    continue;
                }

                for (var k = 0; k < joinedKeys.Count; k++)
                {
                    var key = joinedKeys[k];
                    suggestions.Add(new JoinSuggestion(
                        $"Join on {key} = {column}",
                        "Heuristic match (reverse)",
                        $"{Qualified(joinedAlias, key)} = {Qualified(otherAlias, column)}",
                        840));
                }
            }
        }

        var ordered = suggestions
            .GroupBy(suggestion => suggestion.InsertText, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(suggestion => suggestion.Score).First())
            .Where(suggestion => Matches(suggestion.Label, needle))
            .OrderByDescending(suggestion => suggestion.Score)
            .ThenBy(suggestion => suggestion.Label, StringComparer.OrdinalIgnoreCase)
            .Take(JoinLimit);

        var rank = 0;
        foreach (var suggestion in ordered)
        {
            results.Add(new CompletionItem
            {
                Label = suggestion.Label,
                Detail = suggestion.Detail,
                Kind = CompletionKind.Join,
                InsertText = suggestion.InsertText,
                RankSort = rank++,
            });
        }
    }

    private sealed record JoinSuggestion(string Label, string Detail, string InsertText, int Score);

    private sealed class CompletionItemComparer : IComparer<CompletionItem>
    {
        public static CompletionItemComparer Instance { get; } = new();

        public int Compare(CompletionItem? x, CompletionItem? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var cmp = x.MatchRank.CompareTo(y.MatchRank);
            if (cmp != 0) return cmp;

            cmp = x.Rank.CompareTo(y.Rank);
            if (cmp != 0) return cmp;

            cmp = x.RankSort.CompareTo(y.RankSort);
            if (cmp != 0) return cmp;

            cmp = x.Label.Length.CompareTo(y.Label.Length);
            if (cmp != 0) return cmp;

            cmp = string.Compare(x.Label, y.Label, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;

            return string.Compare(x.Detail, y.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<CompletionItem> Finish(List<CompletionItem> results, int max)
    {
        if (results.Count > 1)
        {
            results.Sort(CompletionItemComparer.Instance);
        }

        if (results.Count > max)
        {
            results.RemoveRange(max, results.Count - max);
        }

        return results;
    }

    private static bool Matches(string candidate, string needle) =>
        needle.Length == 0 || candidate.StartsWith(needle, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(ReadOnlySpan<char> candidate, string needle) =>
        needle.Length == 0 || candidate.StartsWith(needle, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- statement parsing

    [GeneratedRegex(
        @"\b(?:FROM|JOIN)\s+(?<table>(?:\[[^\]]+\]|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_][\w$#@]*))?)(?:\s+(?:AS\s+)?(?<alias>\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex TableReferencePattern();

    [GeneratedRegex(
        @"^\s*UPDATE\s+(?<table>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*))?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UpdateStatementPattern();

    [GeneratedRegex(
        @"^\s*INSERT\s+(?:INTO\s+)?(?<table>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*))?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex InsertStatementPattern();

    [GeneratedRegex(@"\bSET\b", RegexOptions.IgnoreCase)]
    private static partial Regex SetKeywordPattern();

    [GeneratedRegex(@"\bWHERE\b", RegexOptions.IgnoreCase)]
    private static partial Regex WhereKeywordPattern();

    [GeneratedRegex(@"\bDECLARE\b(?<body>[^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DeclarePattern();

    [GeneratedRegex(@"@[A-Za-z_][\w$#@]*")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"^\s*WITH\b", RegexOptions.IgnoreCase)]
    private static partial Regex WithClausePattern();

    [GeneratedRegex(@"^\s*SELECT\s+(?:DISTINCT\s+)?(?:TOP\s+\d+\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex SelectListPattern();

    [GeneratedRegex(@"\bAS\s+(?<alias>\[?[\w$#@]+\]?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SelectAliasPattern();

    [GeneratedRegex(@"^\[?[\w$#@]+\]?(?:\s*\.\s*\[?[\w$#@]+\]?)*$")]
    private static partial Regex SimpleColumnPattern();

    /// <summary>Start of the statement under the caret (after the previous semicolon).</summary>
    public static int StatementStart(string text, int caret) =>        caret > 0 ? text.LastIndexOf(';', caret - 1) + 1 : 0;

    /// <summary>
    /// The end of the statement under the caret: its next semicolon, or the document end. Aliases
    /// and FROM/JOIN below the caret must still count — the SELECT list is written above the FROM.
    /// </summary>
    private static int StatementEnd(string text, int caret)
    {
        var index = text.IndexOf(';', Math.Clamp(caret, 0, text.Length));
        return index < 0 ? text.Length : index;
    }

    /// <summary>Columns a CTE exposes, best effort from its body when no column list is given.</summary>
    private static TableSchema BuildCte(
        string name,
        IReadOnlyList<string> explicitColumns,
        string body,
        IReadOnlyList<TableSchema> tables)
    {
        if (explicitColumns.Count > 0)
        {
            return new TableSchema { Name = name, Columns = explicitColumns };
        }

        var source = FindTableReferences(body, body.Length, tables)
            .FirstOrDefault(reference => reference.Table is not null)
            ?.Table;

        var columns = SelectListColumns(body);
        return new TableSchema
        {
            Name = name,
            Columns = columns.Count > 0 ? columns : source?.Columns ?? [],
        };
    }

    private static IReadOnlyList<string> SelectListColumns(string body)
    {
        var select = SelectListPattern().Match(body);
        if (!select.Success)
        {
            return [];
        }

        var from = TopLevelKeyword(body, "FROM", select.Index + select.Length);
        if (from < 0)
        {
            return [];
        }

        var columns = new List<string>();
        foreach (var item in SplitTopLevel(body[(select.Index + select.Length)..from], ','))
        {
            var expression = item.Trim();
            if (expression.Length == 0)
            {
                continue;
            }

            // A wildcard means the real column set is the source table; let the caller use it.
            if (expression == "*" || expression.EndsWith(".*", StringComparison.Ordinal))
            {
                return [];
            }

            var alias = SelectAliasPattern().Match(expression);
            if (alias.Success)
            {
                columns.Add(NormalizeIdentifier(alias.Groups["alias"].Value));
                continue;
            }

            if (SimpleColumnPattern().IsMatch(expression))
            {
                columns.Add(NormalizeIdentifier(expression));
            }
        }

        return columns;
    }

    /// <summary>
    /// The SELECT list aligned with the result's columns: one entry per projected item, or null for
    /// an item that is an expression rather than a plain column. Returns null as a whole when the
    /// list cannot be aligned (a wildcard, no FROM, or no recognisable SELECT), so callers keep the
    /// name-only fallback instead of guessing.
    /// <para>
    /// The qualifier is what makes a join safely editable: <c>[s].[Id]</c> names Settings' key even
    /// when <c>Id</c> also exists on the joined table, which name matching alone cannot resolve.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SelectProjection?>? SelectProjections(string statement)
    {
        var select = SelectListPattern().Match(statement);
        if (!select.Success)
        {
            return null;
        }

        var from = TopLevelKeyword(statement, "FROM", select.Index + select.Length);
        if (from < 0)
        {
            return null;
        }

        var items = SplitTopLevel(statement[(select.Index + select.Length)..from], ',').ToList();
        var projections = new List<SelectProjection?>(items.Count);

        foreach (var item in items)
        {
            var expression = item.Trim();
            if (expression.Length == 0)
            {
                return null;
            }

            // A wildcard stands for a column set we would have to expand from the table.
            if (expression is "*" || expression.EndsWith(".*", StringComparison.Ordinal))
            {
                return null;
            }

            string? alias = null;
            var aliasMatch = SelectAliasPattern().Match(expression);
            if (aliasMatch.Success)
            {
                alias = NormalizeIdentifier(aliasMatch.Groups["alias"].Value);
                expression = expression[..aliasMatch.Index].TrimEnd();
            }

            if (!SimpleColumnPattern().IsMatch(expression))
            {
                projections.Add(null);
                continue;
            }

            var parts = SplitIdentifierPath(expression);
            if (parts.Count is 0 or > 3 || parts.Any(part => part.Length == 0))
            {
                projections.Add(null);
                continue;
            }

            var column = parts[^1];
            var qualifier = parts.Count switch
            {
                1 => null,
                2 => parts[0],
                _ => $"{parts[0]}.{parts[1]}",
            };

            projections.Add(new SelectProjection(qualifier, column, alias ?? column));
        }

        return projections;
    }

    /// <summary>Splits <c>[dbo].[Settings].[Id]</c> into its parts, trimming brackets and quotes.</summary>
    private static IReadOnlyList<string> SplitIdentifierPath(string text)
    {
        var parts = new List<string>(3);
        var start = 0;
        var bracketed = false;
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (bracketed)
            {
                if (current == ']')
                {
                    bracketed = false;
                }

                continue;
            }

            if (quoted)
            {
                if (current == '"')
                {
                    quoted = false;
                }

                continue;
            }

            switch (current)
            {
                case '[':
                    bracketed = true;
                    break;
                case '"':
                    quoted = true;
                    break;
                case '.':
                    parts.Add(NormalizeIdentifier(text[start..index]));
                    start = index + 1;
                    break;
            }
        }

        parts.Add(NormalizeIdentifier(text[start..]));
        return parts;
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    break;
                default:
                    if (text[index] == separator && depth == 0)
                    {
                        yield return text[start..index];
                        start = index + 1;
                    }

                    break;
            }
        }

        yield return text[start..];
    }

    private static int TopLevelKeyword(string text, string keyword, int start)
    {
        var depth = 0;
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
                continue;
            }

            if (text[index] == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth != 0 ||
                (index > 0 && IsWordChar(text[index - 1])) ||
                index + keyword.Length > text.Length ||
                string.Compare(text, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0 ||
                (index + keyword.Length < text.Length && IsWordChar(text[index + keyword.Length])))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static TableSchema? ResolveQualifier(
        IReadOnlyList<TableReference> references,
        string qualifier,
        IReadOnlyList<TableSchema> tables,
        IReadOnlyList<TableSchema> ctes)
    {
        foreach (var reference in references)
        {
            if (reference.Table is not null &&
                string.Equals(reference.Alias, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return reference.Table;
            }
        }

        return FindTable(qualifier, ctes) ?? FindTable(qualifier, tables);
    }

    private static TableSchema? FindTable(string name, IReadOnlyList<TableSchema> tables) =>
        FindTable(name, null, tables);

    private static TableSchema? FindTable(string name, string? schema, IReadOnlyList<TableSchema> tables)
    {
        // A schema-qualified reference must reach the table in that schema. Without a schema (a
        // bare name, a CTE, or an alias lookup) the first name match is the historical behaviour.
        TableSchema? firstByName = null;
        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (!table.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (schema is null)
            {
                return table;
            }

            if (string.Equals(table.Schema, schema, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }

            firstByName ??= table;
        }

        return firstByName;
    }

    /// <summary>Strips schema qualification and quoting: <c>[dbo].[Orders]</c> -> <c>Orders</c>.</summary>
    private static string NormalizeIdentifier(string token)
    {
        var trimmed = token.Trim();
        var dot = trimmed.LastIndexOf('.');
        if (dot >= 0)
        {
            trimmed = trimmed[(dot + 1)..];
        }

        return trimmed.Trim('[', ']', '"', '\'', ' ');
    }

    /// <summary>Splits a possibly-qualified name: <c>[dbo].[Orders]</c> -> (<c>dbo</c>, <c>Orders</c>).</summary>
    private static (string? Schema, string Name) SplitQualified(string token)
    {
        var trimmed = token.Trim();
        var dot = trimmed.LastIndexOf('.');
        return dot < 0
            ? (null, NormalizeIdentifier(trimmed))
            : (NormalizeIdentifier(trimmed[..dot]), NormalizeIdentifier(trimmed[(dot + 1)..]));
    }

    // ---------------------------------------------------------------- alias + join heuristics

    /// <summary>
    /// Alias for a table name, as the desktop app generates it: CamelCase initials
    /// (<c>ApplicationUser</c> -> <c>au</c>), snake_case initials (<c>order_items</c> -> <c>oi</c>),
    /// otherwise the first letter (<c>customers</c> -> <c>c</c>).
    /// </summary>
    private static string GenerateTableAlias(string tableName)
    {
        var span = tableName.AsSpan();
        Span<char> buffer = stackalloc char[64];
        var count = 0;

        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (char.IsUpper(ch))
            {
                if (count < buffer.Length)
                {
                    buffer[count] = char.ToLowerInvariant(ch);
                }

                count++;
            }
        }

        if (count > 1)
        {
            if (count <= buffer.Length)
            {
                return new string(buffer.Slice(0, count));
            }

            var large = new char[count];
            var pos = 0;
            for (var i = 0; i < span.Length; i++)
            {
                if (char.IsUpper(span[i]))
                {
                    large[pos++] = char.ToLowerInvariant(span[i]);
                }
            }

            return new string(large);
        }

        // snake_case initials (order_items -> oi).
        count = 0;
        var atSegmentStart = true;
        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (ch == '_')
            {
                atSegmentStart = true;
            }
            else if (atSegmentStart)
            {
                if (count < buffer.Length)
                {
                    buffer[count] = char.ToLowerInvariant(ch);
                }

                count++;
                atSegmentStart = false;
            }
        }

        if (count > 1)
        {
            if (count <= buffer.Length)
            {
                return new string(buffer.Slice(0, count));
            }

            var large = new char[count];
            var pos = 0;
            atSegmentStart = true;
            for (var i = 0; i < span.Length; i++)
            {
                var ch = span[i];
                if (ch == '_')
                {
                    atSegmentStart = true;
                }
                else if (atSegmentStart)
                {
                    large[pos++] = char.ToLowerInvariant(ch);
                    atSegmentStart = false;
                }
            }

            return new string(large);
        }

        return span.Length > 0 ? char.ToLowerInvariant(span[0]).ToString() : "t";
    }

    private static string GenerateUniqueAlias(string tableName, HashSet<string>? usedAliases)
    {
        var baseAlias = GenerateTableAlias(tableName);
        if (usedAliases is null || !usedAliases.Contains(baseAlias))
        {
            return baseAlias;
        }

        var alias = baseAlias;
        var counter = 1;
        while (usedAliases.Contains(alias))
        {
            alias = $"{baseAlias}{counter++}";
        }

        return alias;
    }

    /// <summary>A foreign key points at this table, honouring the schema when the provider reports one.</summary>
    private static bool ForeignKeyTargets(ForeignKeySchema key, TableSchema table) =>
        key.ReferencedTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(key.ReferencedSchema)
            || string.Equals(key.ReferencedSchema, table.Schema, StringComparison.OrdinalIgnoreCase));

    /// <summary>Key columns usable as the "one" side of a join: the primary key, else <c>id</c>.</summary>
    private static IReadOnlyList<string> PreferredKeyColumns(TableSchema table)
    {
        if (table.PrimaryKey.Count > 0)
        {
            return table.PrimaryKey;
        }

        List<string>? ids = null;
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            if (column.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                ids ??= [];
                ids.Add(column);
            }
        }

        return ids ?? (IReadOnlyList<string>)[];
    }

    /// <summary><c>customer_id</c> -> <c>customer</c>; null when the column is not an id column.</summary>
    private static string? ColumnStemFromNormalized(string normalized) =>
        normalized.Length > 2 && normalized.EndsWith("id", StringComparison.Ordinal)
            ? normalized[..^2]
            : null;

    /// <summary>True when an <c>xxx_id</c> stem names the table (singular or plural).</summary>
    private static bool MatchesTableStem(string stem, string tableName)
    {
        var normalized = NormalizeName(tableName);
        if (normalized.Length == 0 || stem.Length == 0)
        {
            return false;
        }

        if (StemMatchesVariant(stem, normalized))
        {
            return true;
        }

        var singular = Singularize(normalized);
        return !ReferenceEquals(singular, normalized) && singular.Length > 0 && StemMatchesVariant(stem, singular);
    }

    private static bool StemMatchesVariant(string stem, string variant) =>
        stem == variant
        || stem.EndsWith(variant, StringComparison.Ordinal)
        || variant.EndsWith(stem, StringComparison.Ordinal);

    private static string Singularize(string value)
    {
        if (value.EndsWith("ies", StringComparison.Ordinal))
        {
            return $"{value[..^3]}y";
        }

        if (value.EndsWith("ses", StringComparison.Ordinal))
        {
            return value[..^2];
        }

        if (value.EndsWith('s') && !value.EndsWith("ss", StringComparison.Ordinal))
        {
            return value[..^1];
        }

        return value;
    }

    private static string NormalizeName(string value)
    {
        var span = value.AsSpan();
        Span<char> buffer = span.Length <= 128 ? stackalloc char[span.Length] : new char[span.Length];
        var len = 0;
        for (var i = 0; i < span.Length; i++)
        {
            var character = span[i];
            if (char.IsLetterOrDigit(character))
            {
                buffer[len++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer.Slice(0, len));
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';
}
