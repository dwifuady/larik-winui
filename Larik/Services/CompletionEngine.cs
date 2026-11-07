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
}

/// <summary>The table an UPDATE/INSERT writes, when the caret is in its column list.</summary>
public sealed record StatementTarget(TableSchema Table, bool Assignment);

/// <summary>A table named by FROM/JOIN in the statement under the caret.</summary>
public sealed record TableReference(TableSchema? Table, string Name, string? Alias);

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
    string Clipboard);

/// <summary>
/// Snippet + keyword + schema completion, modelled on the desktop app's Monaco provider
/// (snippets match on trigger prefix, keywords and schema objects fill the rest, tables gain
/// aliases and joins are filled from the columns the statement already references).
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
    private static readonly HashSet<string> TableContext = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "OUTER JOIN",
        "CROSS JOIN", "APPLY", "CROSS APPLY", "OUTER APPLY", "INTO", "UPDATE", "TABLE",
    };

    /// <summary>Table positions where a generated alias is part of the suggestion.</summary>
    private static readonly HashSet<string> AliasContext = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "OUTER JOIN",
        "CROSS JOIN", "APPLY", "CROSS APPLY", "OUTER APPLY",
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
    /// What the caret is positioned to type, and which keyword put it there. Only the statement
    /// under the caret counts, and the word being typed is ignored so "FROM cust" still reads as
    /// a table position.
    /// </summary>
    public static (CompletionScope Scope, string? Keyword) DetectScope(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);

        var (wordStart, _, _) = ExtractContext(text, caret);
        var start = StatementStart(text, caret);
        var before = text[start..Math.Max(start, Math.Min(wordStart, caret))].TrimEnd();
        if (before.Length == 0)
        {
            return (CompletionScope.Keyword, null);
        }

        // "USE [name" is still typing an identifier; drop the bracket before reading words.
        if (before.EndsWith('['))
        {
            before = before[..^1].TrimEnd();
        }

        // Operators and separators are followed by a column, not by a keyword.
        if (before.Length > 0 && before[^1] is '=' or '<' or '>' or '!' or ',' or '(')
        {
            return (CompletionScope.Column, null);
        }

        var words = before.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return (CompletionScope.Keyword, null);
        }

        // USE <database> at the start of a batch.
        if (words.Length == 1 && words[0].Equals("USE", StringComparison.OrdinalIgnoreCase))
        {
            return (CompletionScope.Database, "USE");
        }

        var last = words[^1].ToUpperInvariant();
        var combined = words.Length > 1 ? $"{words[^2]} {last}".ToUpperInvariant() : last;

        if (TableContext.Contains(combined))
        {
            return (CompletionScope.Table, combined);
        }

        if (TableContext.Contains(last))
        {
            return (CompletionScope.Table, last);
        }

        if (ColumnContext.Contains(combined))
        {
            return (CompletionScope.Column, combined);
        }

        if (ColumnContext.Contains(last))
        {
            return (CompletionScope.Column, last);
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

        var statement = text[start..caret];
        var references = new List<TableReference>();
        foreach (Match match in TableReferencePattern().Matches(statement))
        {
            if (DepthAt(statement, match.Index) != 0)
            {
                continue;
            }

            var name = NormalizeIdentifier(match.Groups["table"].Value);
            var table = FindTable(name, ctes) ?? FindTable(name, tables);

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
            var table = FindTable(NormalizeIdentifier(insert.Groups["table"].Value), tables);
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

        var updated = FindTable(NormalizeIdentifier(update.Groups["table"].Value), tables);
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
        var needle = request.Word.ToLowerInvariant();
        var results = new List<CompletionItem>();

        if (request.Qualifier is not null)
        {
            // alias. or table. -> that table's columns, whatever the clause.
            var table = ResolveQualifier(request.References, request.Qualifier, request.Tables, request.Ctes);
            IReadOnlyList<TableSchema> sources;
            if (table is not null)
            {
                sources = [table];
            }
            else
            {
                var mentioned = request.References
                    .Where(reference => reference.Table is not null)
                    .Select(reference => reference.Table!)
                    .Distinct()
                    .ToList();

                sources = mentioned.Count > 0 ? mentioned : request.Tables;
            }

            AddColumns(results, sources, needle);
            return Finish(results, SchemaLimit);
        }

        switch (request.Scope)
        {
            case CompletionScope.Database:
                AddDatabases(results, request.Databases, needle);
                break;

            case CompletionScope.Table:
                AddCtes(results, request.Ctes, needle);
                AddTables(results, request, needle);
                break;

            case CompletionScope.Column:
                if (request.Target is { } target)
                {
                    AddTargetColumns(results, target, needle);
                    break;
                }

                AddVariables(results, request.Variables, needle);
                AddJoinConditions(results, request, needle);
                AddScopedColumns(results, request, needle);
                AddCteColumns(results, request, needle);
                AddFunctions(results, needle);
                break;

            default:
                if (request.Word.Length == 0)
                {
                    return [];
                }

                AddSnippets(results, request.Snippets, request.Word, request.Clipboard);
                AddVariables(results, request.Variables, needle);
                AddKeywords(results, needle);
                AddPlainTables(results, request.Tables, needle);
                AddColumns(results, request.Tables, needle);
                break;
        }

        // A table or database list is what these clauses are for: show them all, the popup scrolls.
        var max = request.Scope switch
        {
            CompletionScope.Table or CompletionScope.Database => int.MaxValue,
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
        foreach (var keyword in Keywords.Where(keyword => Matches(keyword, needle)))
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

    private static void AddFunctions(List<CompletionItem> results, string needle)
    {
        foreach (var function in Functions.Where(function => Matches(function, needle)))
        {
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

    /// <summary>Tables in a table position; after FROM/JOIN each one carries a generated alias.</summary>
    private static void AddTables(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        if (request.Keyword is null || !AliasContext.Contains(request.Keyword))
        {
            // UPDATE / INSERT INTO / TABLE positions take the bare name.
            AddPlainTables(results, request.Tables, needle);
            return;
        }

        var used = new HashSet<string>(
            request.References.Where(reference => reference.Alias is not null).Select(reference => reference.Alias!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var table in request.Tables.Where(table => Matches(table.Name, needle)))
        {
            var alias = GenerateUniqueAlias(table.Name, used);
            results.Add(new CompletionItem
            {
                Label = table.Name,
                Detail = $"table · AS {alias}",
                Kind = CompletionKind.Table,
                InsertText = $"{table.Name} AS {alias}",
                RankSort = 0,
            });

            results.Add(new CompletionItem
            {
                Label = $"{table.Name} (no alias)",
                Detail = "table · no alias",
                Kind = CompletionKind.Table,
                InsertText = table.Name,
                RankSort = 1,
            });
        }
    }

    private static void AddPlainTables(List<CompletionItem> results, IReadOnlyList<TableSchema> tables, string needle)
    {
        foreach (var table in tables.Where(table => Matches(table.Name, needle)))
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

    private static void AddCtes(List<CompletionItem> results, IReadOnlyList<TableSchema> ctes, string needle)
    {
        foreach (var cte in ctes.Where(cte => Matches(cte.Name, needle)))
        {
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
        var referenced = new HashSet<string>(
            request.References
                .Where(reference => reference.Table is not null)
                .Select(reference => reference.Table!.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var cte in request.Ctes)
        {
            if (referenced.Contains(cte.Name))
            {
                continue;
            }

            foreach (var column in cte.Columns.Where(column => Matches(column, needle)))
            {
                results.Add(new CompletionItem
                {
                    Label = column,
                    Detail = $"CTE · {cte.Name}.{column}",
                    Kind = CompletionKind.Column,
                    InsertText = column,
                });
            }
        }
    }

    /// <summary>Columns of the statement's tables; aliased tables are inserted as <c>alias.column</c>.</summary>
    private static void AddScopedColumns(List<CompletionItem> results, CompletionRequest request, string needle)
    {
        if (request.References.Count == 0)
        {
            AddColumns(results, request.Tables, needle);
            return;
        }

        foreach (var reference in request.References.DistinctBy(
                     reference => reference.Alias ?? reference.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (reference.Table is not { } table)
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                var label = reference.Alias is null ? column : $"{reference.Alias}.{column}";

                // Typing the column name should find it even when the alias is prefixed.
                if (!Matches(column, needle) && !(reference.Alias is not null && Matches(label, needle)))
                {
                    continue;
                }

                results.Add(new CompletionItem
                {
                    Label = label,
                    Detail = $"{table.Name}.{column}",
                    Kind = CompletionKind.Column,
                    InsertText = label,
                });
            }
        }
    }

    private static void AddColumns(List<CompletionItem> results, IReadOnlyList<TableSchema> tables, string needle)
    {
        foreach (var table in tables)
        {
            foreach (var column in table.Columns.Where(column => Matches(column, needle)))
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

    /// <summary>Columns of the table an UPDATE/INSERT writes; SET positions ask for <c>column = </c>.</summary>
    private static void AddTargetColumns(List<CompletionItem> results, StatementTarget target, string needle)
    {
        foreach (var column in target.Table.Columns.Where(column => Matches(column, needle)))
        {
            results.Add(new CompletionItem
            {
                Label = column,
                Detail = $"{target.Table.Name}.{column}",
                Kind = CompletionKind.Column,
                InsertText = target.Assignment ? $"{column} = " : column,
            });
        }
    }

    private static void AddVariables(List<CompletionItem> results, IReadOnlyList<string> variables, string needle)
    {
        foreach (var variable in variables)
        {
            // The label carries the '@'; match what the user types after it too.
            if (!Matches(variable, needle) && !Matches(variable.TrimStart('@'), needle))
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
        foreach (var database in databases.Where(database => Matches(database, needle)))
        {
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

        foreach (var other in prior)
        {
            var otherTable = other.Table!;
            var otherAlias = other.Alias ?? otherTable.Name;

            // Declared foreign keys: joined.customer_id -> other.id (and the reverse direction).
            foreach (var group in joinedTable.ForeignKeys
                         .Where(key => key.ReferencedTable.Equals(otherTable.Name, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(key => key.Constraint, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                suggestions.Add(new JoinSuggestion(
                    $"Join on {first.Column} = {first.ReferencedColumn}",
                    "Foreign key",
                    string.Join(" AND ", group.Select(key => $"{joinedAlias}.{key.Column} = {otherAlias}.{key.ReferencedColumn}")),
                    1000 - (group.Count() - 1)));
            }

            foreach (var group in otherTable.ForeignKeys
                         .Where(key => key.ReferencedTable.Equals(joinedTable.Name, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(key => key.Constraint, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                suggestions.Add(new JoinSuggestion(
                    $"Join on {first.ReferencedColumn} = {first.Column}",
                    "Foreign key (reverse)",
                    string.Join(" AND ", group.Select(key => $"{joinedAlias}.{key.ReferencedColumn} = {otherAlias}.{key.Column}")),
                    980 - (group.Count() - 1)));
            }

            // joined.customer_id -> other.id
            var otherKeys = PreferredKeyColumns(otherTable);
            foreach (var column in joinedTable.Columns)
            {
                var stem = ColumnStem(column);
                if (stem is null || otherKeys.Count == 0 || !MatchesTableStem(stem, otherTable.Name))
                {
                    continue;
                }

                foreach (var key in otherKeys)
                {
                    suggestions.Add(new JoinSuggestion(
                        $"Join on {column} = {key}",
                        "Heuristic match",
                        $"{joinedAlias}.{column} = {otherAlias}.{key}",
                        900));
                }
            }

            // Columns with the same name on both sides (id = id, status = status, ...).
            foreach (var column in joinedTable.Columns)
            {
                foreach (var otherColumn in otherTable.Columns)
                {
                    if (!NormalizeName(column).Equals(NormalizeName(otherColumn), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var isKey = joinedTable.PrimaryKey.Contains(column, StringComparer.OrdinalIgnoreCase)
                                || otherTable.PrimaryKey.Contains(otherColumn, StringComparer.OrdinalIgnoreCase);

                    suggestions.Add(new JoinSuggestion(
                        $"Join on {column} = {otherColumn}",
                        "Heuristic match",
                        $"{joinedAlias}.{column} = {otherAlias}.{otherColumn}",
                        isKey ? 760 : 720));
                }
            }

            // other.customer_id -> joined.id (the reverse direction).
            var joinedKeys = PreferredKeyColumns(joinedTable);
            foreach (var column in otherTable.Columns)
            {
                var stem = ColumnStem(column);
                if (stem is null || joinedKeys.Count == 0 || !MatchesTableStem(stem, joinedTable.Name))
                {
                    continue;
                }

                foreach (var key in joinedKeys)
                {
                    suggestions.Add(new JoinSuggestion(
                        $"Join on {key} = {column}",
                        "Heuristic match (reverse)",
                        $"{joinedAlias}.{key} = {otherAlias}.{column}",
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

    private static IReadOnlyList<CompletionItem> Finish(List<CompletionItem> results, int max) =>
        results
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.RankSort)
            .ThenBy(item => item.Label.Length)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Detail, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();

    private static bool Matches(string candidate, string needle) =>
        needle.Length == 0 || candidate.StartsWith(needle, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- statement parsing

    [GeneratedRegex(
        @"\b(?:FROM|JOIN)\s+(?<table>(?:\[[^\]]+\]|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_][\w$#@]*))?)(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_][\w$#@]*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex TableReferencePattern();

    [GeneratedRegex(@"^\s*UPDATE\s+(?<table>\[[^\]]+\]|[A-Za-z_][\w$#@]*)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateStatementPattern();

    [GeneratedRegex(@"^\s*INSERT\s+(?:INTO\s+)?(?<table>\[[^\]]+\]|[A-Za-z_][\w$#@]*)", RegexOptions.IgnoreCase)]
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

    private static int StatementStart(string text, int caret) =>
        caret > 0 ? text.LastIndexOf(';', caret - 1) + 1 : 0;

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

    private static int DepthAt(string text, int index)
    {
        var depth = 0;
        for (var cursor = 0; cursor < index && cursor < text.Length; cursor++)
        {
            if (text[cursor] == '(')
            {
                depth++;
            }
            else if (text[cursor] == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
        }

        return depth;
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
        tables.FirstOrDefault(table => table.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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

    // ---------------------------------------------------------------- alias + join heuristics

    /// <summary>
    /// Alias for a table name, as the desktop app generates it: CamelCase initials
    /// (<c>ApplicationUser</c> -> <c>au</c>), snake_case initials (<c>order_items</c> -> <c>oi</c>),
    /// otherwise the first letter (<c>customers</c> -> <c>c</c>).
    /// </summary>
    private static string GenerateTableAlias(string tableName)
    {
        var capitals = tableName.Where(char.IsUpper).ToArray();
        if (capitals.Length > 1)
        {
            return new string(capitals).ToLowerInvariant();
        }

        var parts = tableName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            return new string(parts.Select(part => char.ToLowerInvariant(part[0])).ToArray());
        }

        return tableName.Length > 0 ? char.ToLowerInvariant(tableName[0]).ToString() : "t";
    }

    private static string GenerateUniqueAlias(string tableName, ISet<string> usedAliases)
    {
        var baseAlias = GenerateTableAlias(tableName);
        var alias = baseAlias;
        var counter = 1;

        while (usedAliases.Contains(alias))
        {
            alias = $"{baseAlias}{counter++}";
        }

        return alias;
    }

    /// <summary>Key columns usable as the "one" side of a join: the primary key, else <c>id</c>.</summary>
    private static IReadOnlyList<string> PreferredKeyColumns(TableSchema table) =>
        table.PrimaryKey.Count > 0
            ? table.PrimaryKey
            : table.Columns.Where(column => column.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary><c>customer_id</c> -> <c>customer</c>; null when the column is not an id column.</summary>
    private static string? ColumnStem(string column)
    {
        var normalized = NormalizeName(column);
        return normalized.Length > 2 && normalized.EndsWith("id", StringComparison.Ordinal)
            ? normalized[..^2]
            : null;
    }

    /// <summary>True when an <c>xxx_id</c> stem names the table (singular or plural).</summary>
    private static bool MatchesTableStem(string stem, string tableName)
    {
        var normalized = NormalizeName(tableName);
        if (normalized.Length == 0 || stem.Length == 0)
        {
            return false;
        }

        foreach (var variant in new[] { normalized, Singularize(normalized) })
        {
            if (variant.Length > 0 &&
                (stem == variant ||
                 stem.EndsWith(variant, StringComparison.Ordinal) ||
                 variant.EndsWith(stem, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

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
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';
}
