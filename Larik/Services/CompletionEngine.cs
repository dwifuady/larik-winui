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
}

/// <summary>Everything the engine needs to answer one query, resolved by the completion source.</summary>
public sealed record CompletionRequest(
    string Word,
    string? Qualifier,
    CompletionScope Scope,
    IReadOnlyList<SnippetDef> Snippets,
    IReadOnlyList<TableSchema> Tables,
    IReadOnlyList<TableSchema> ScopedTables,
    TableSchema? QualifiedTable,
    string Clipboard);

/// <summary>
/// Snippet + keyword + schema completion, modelled on the desktop app's Monaco provider
/// (snippets match on trigger prefix, keywords and schema objects fill the rest).
/// </summary>
public static partial class CompletionEngine
{
    public static IReadOnlyList<string> Keywords { get; } =
    [
        "SELECT", "FROM", "WHERE", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "ON",
        "GROUP BY", "ORDER BY", "HAVING", "TOP", "DISTINCT", "AS", "AND", "OR", "NOT", "NULL", "IS NULL",
        "IN", "EXISTS", "BETWEEN", "LIKE", "CASE WHEN", "ELSE", "END", "UNION", "UNION ALL", "INSERT INTO",
        "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "TRUNCATE TABLE",
        "BEGIN TRANSACTION", "COMMIT", "ROLLBACK", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "ROW_NUMBER",
        "OVER", "PARTITION BY", "CAST", "GETDATE", "SYSDATETIME", "DATEADD", "DATEDIFF", "FORMAT",
    ];

    /// <summary>Words after which only a table name makes sense.</summary>
    private static readonly HashSet<string> TableContext = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "OUTER JOIN",
        "CROSS JOIN", "APPLY", "CROSS APPLY", "OUTER APPLY", "INTO", "UPDATE", "TABLE",
    };

    /// <summary>Words after which column names make sense.</summary>
    private static readonly HashSet<string> ColumnContext = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WHERE", "ON", "AND", "OR", "SET", "HAVING", "ORDER BY", "GROUP BY", "BY",
        "DISTINCT", "LIKE", "IN", "BETWEEN", "IS", "NOT", "THEN", "WHEN",
    };

    /// <summary>Words that follow a table reference but are not aliases.</summary>
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "ON", "GROUP",
        "ORDER", "HAVING", "SET", "UNION", "EXCEPT", "INTERSECT", "AS", "WITH", "SELECT",
        "FROM", "AND", "OR", "APPLY", "TABLESAMPLE", "PIVOT", "UNPIVOT", "FOR", "OPTION",
    };

    private const int KeywordLimit = 8;
    private const int SchemaLimit = 500;
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
            var dot = start - 1;
            var qStart = dot;
            while (qStart > 0 && IsWordChar(text[qStart - 1]))
            {
                qStart--;
            }

            if (qStart < dot)
            {
                qualifier = text[qStart..dot];
            }
        }

        return (start, word, qualifier);
    }

    /// <summary>
    /// What the caret is positioned to type. Only the statement under the caret counts, and the
    /// word being typed is ignored so "FROM cust" still reads as a table position.
    /// </summary>
    public static CompletionScope DetectScope(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);

        var (wordStart, _, _) = ExtractContext(text, caret);
        var start = StatementStart(text, caret);
        var before = text[start..Math.Max(start, Math.Min(wordStart, caret))].TrimEnd();
        if (before.Length == 0)
        {
            return CompletionScope.Keyword;
        }

        // Operators and separators are followed by a column, not by a keyword.
        if (before[^1] is '=' or '<' or '>' or '!' or ',' or '(')
        {
            return CompletionScope.Column;
        }

        var words = before.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return CompletionScope.Keyword;
        }

        var last = words[^1].ToUpperInvariant();
        var combined = words.Length > 1 ? $"{words[^2]} {last}".ToUpperInvariant() : last;

        if (TableContext.Contains(combined) || TableContext.Contains(last))
        {
            return CompletionScope.Table;
        }

        if (ColumnContext.Contains(combined) || ColumnContext.Contains(last))
        {
            return CompletionScope.Column;
        }

        return CompletionScope.Keyword;
    }

    /// <summary>Tables named in the statement under the caret, used to focus column completion.</summary>
    public static IReadOnlyList<TableSchema> TablesInStatement(string text, int caret, IReadOnlyList<TableSchema> tables)
    {
        var found = new List<TableSchema>();
        foreach (var (table, _) in TableReferences(text, caret, tables))
        {
            if (table is not null && !found.Contains(table))
            {
                found.Add(table);
            }
        }

        return found;
    }

    /// <summary>Resolves <c>alias.</c> or <c>table.</c> to the table whose columns should be offered.</summary>
    public static TableSchema? ResolveQualifier(string text, int caret, string qualifier, IReadOnlyList<TableSchema> tables)
    {
        foreach (var (table, alias) in TableReferences(text, caret, tables))
        {
            if (table is not null && string.Equals(alias, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        return FindTable(qualifier, tables);
    }

    public static IReadOnlyList<CompletionItem> Suggest(CompletionRequest request)
    {
        var needle = request.Word.ToLowerInvariant();
        var results = new List<CompletionItem>();

        if (request.Qualifier is not null)
        {
            // alias. or table. -> that table's columns, whatever the clause.
            IReadOnlyList<TableSchema> sources = request.QualifiedTable is { } table ? [table] : request.Tables;
            AddColumns(results, sources, needle);
            return Finish(results, SchemaLimit);
        }

        switch (request.Scope)
        {
            case CompletionScope.Table:
                AddTables(results, request.Tables, needle);
                break;

            case CompletionScope.Column:
                AddColumns(results, request.ScopedTables.Count > 0 ? request.ScopedTables : request.Tables, needle);
                break;

            default:
                if (request.Word.Length == 0)
                {
                    return [];
                }

                AddSnippets(results, request.Snippets, request.Word, request.Clipboard);
                AddKeywords(results, needle);
                AddTables(results, request.Tables, needle);
                AddColumns(results, request.Tables, needle);
                break;
        }

        // A table list is what FROM is for: show every table, the popup scrolls.
        var max = request.Scope switch
        {
            CompletionScope.Table => int.MaxValue,
            CompletionScope.Keyword => KeywordLimit,
            _ => SchemaLimit,
        };

        return Finish(results, max);
    }

    /// <summary>
    /// Turns snippet markup into insertable text:
    /// ${cursor} -> final caret, ${n:placeholder} -> selectable placeholder (repeats reuse the first value),
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
                RankSort = triggerMatch ? 0 : 1,
            });
        }
    }

    private static void AddKeywords(List<CompletionItem> results, string needle)
    {
        foreach (var keyword in Keywords.Where(k => Matches(k, needle)))
        {
            results.Add(new CompletionItem
            {
                Label = keyword,
                Detail = "keyword",
                Kind = CompletionKind.Keyword,
                InsertText = keyword,
            });
        }
    }

    private static void AddTables(List<CompletionItem> results, IReadOnlyList<TableSchema> tables, string needle)
    {
        foreach (var table in tables.Where(t => Matches(t.Name, needle)))
        {
            results.Add(new CompletionItem
            {
                Label = table.Name,
                Detail = $"table · {table.Columns.Count} columns",
                Kind = CompletionKind.Table,
                InsertText = table.Name,
            });
        }
    }

    private static void AddColumns(List<CompletionItem> results, IReadOnlyList<TableSchema> tables, string needle)
    {
        foreach (var table in tables)
        {
            foreach (var column in table.Columns.Where(c => Matches(c, needle)))
            {
                results.Add(new CompletionItem
                {
                    Label = column,
                    Detail = $"{table.Name}.{column}",
                    Kind = CompletionKind.Column,
                    InsertText = column,
                });
            }
        }
    }

    private static IReadOnlyList<CompletionItem> Finish(List<CompletionItem> results, int max) =>
        results
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.RankSort)
            .ThenBy(r => r.Label.Length)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Detail, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();

    private static bool Matches(string candidate, string needle) =>
        needle.Length == 0 || candidate.StartsWith(needle, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- statement parsing

    [GeneratedRegex(
        @"\b(?:FROM|JOIN)\s+(?<table>(?:\[[^\]]+\]|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_][\w$#@]*))?)(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_][\w$#@]*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex TableReference();

    private static IEnumerable<(TableSchema? Table, string? Alias)> TableReferences(
        string text,
        int caret,
        IReadOnlyList<TableSchema> tables)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = StatementStart(text, caret);
        if (caret <= start)
        {
            yield break;
        }

        foreach (Match match in TableReference().Matches(text[start..caret]))
        {
            var table = FindTable(NormalizeIdentifier(match.Groups["table"].Value), tables);

            string? alias = null;
            if (match.Groups["alias"].Success)
            {
                var candidate = NormalizeIdentifier(match.Groups["alias"].Value);
                if (!NotAliases.Contains(candidate) &&
                    !string.Equals(candidate, table?.Name, StringComparison.OrdinalIgnoreCase))
                {
                    alias = candidate;
                }
            }

            yield return (table, alias);
        }
    }

    private static int StatementStart(string text, int caret) =>
        caret > 0 ? text.LastIndexOf(';', caret - 1) + 1 : 0;

    private static TableSchema? FindTable(string name, IReadOnlyList<TableSchema> tables) =>
        tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';
}
