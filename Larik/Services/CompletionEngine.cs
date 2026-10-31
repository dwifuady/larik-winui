using System.Text;
using Larik.Data;
using Larik.Models;

namespace Larik.Services;

/// <summary>
/// Snippet + keyword + schema completion, modelled on the desktop app's Monaco provider
/// (snippets match on trigger prefix, keywords and schema objects fill the rest).
/// </summary>
public static class CompletionEngine
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

    public static IReadOnlyList<CompletionItem> Suggest(
        string word,
        string? qualifier,
        IReadOnlyList<SnippetDef> snippets,
        IReadOnlyList<TableSchema> tables,
        string clipboard,
        int max = 8)
    {
        if (string.IsNullOrEmpty(word))
        {
            return [];
        }

        var needle = word.ToLowerInvariant();
        var results = new List<CompletionItem>();

        if (qualifier is not null)
        {
            // Qualified column completion: customers. -> that table's columns.
            var table = tables.FirstOrDefault(t => t.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
            var columns = table?.Columns ?? tables.SelectMany(t => t.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var column in columns.Where(c => c.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new CompletionItem
                {
                    Label = column,
                    Detail = table is null ? "column" : $"{table.Name}.{column}",
                    Kind = CompletionKind.Column,
                    InsertText = column,
                });
            }
        }
        else
        {
            foreach (var snippet in snippets)
            {
                if (snippet.Trigger.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ||
                    snippet.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    var order = snippet.Trigger.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    var expanded = Expand(snippet.Content, clipboard);
                    results.Add(new CompletionItem
                    {
                        Label = snippet.Trigger,
                        Detail = snippet.Name,
                        Kind = CompletionKind.Snippet,
                        InsertText = expanded.Text,
                        CaretOffset = expanded.Caret,
                        Selection = expanded.Selection,
                        RankSort = order,
                    });
                }
            }

            foreach (var keyword in Keywords.Where(k => k.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new CompletionItem
                {
                    Label = keyword,
                    Detail = "keyword",
                    Kind = CompletionKind.Keyword,
                    InsertText = keyword,
                });
            }

            foreach (var table in tables.Where(t => t.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new CompletionItem
                {
                    Label = table.Name,
                    Detail = "table",
                    Kind = CompletionKind.Table,
                    InsertText = table.Name,
                });
            }

            foreach (var column in tables.SelectMany(t => t.Columns).Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(c => c.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new CompletionItem
                {
                    Label = column,
                    Detail = "column",
                    Kind = CompletionKind.Column,
                    InsertText = column,
                });
            }
        }

        return results
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.RankSort)
            .ThenBy(r => r.Label.Length)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
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

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';
}
