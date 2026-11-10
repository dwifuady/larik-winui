using Larik.Data;

namespace Larik.Data.MongoDB;

/// <summary>
/// MongoDB as this app's editor understands it. This is the deliberate deviation from the
/// <c>docs/PROVIDERS.md</c> blueprint (which said back then "a non-SQL engine implements
/// <see cref="IEditorLanguage"/>, do not pretend it is a SQL dialect"): the document surface the
/// user writes here is T-SQL, so the language IS a SQL dialect. That buys highlighting, Ctrl+Enter /
/// F5 splitting, T-SQL completion (collections in <c>FROM</c>, sampled fields, keywords) and the
/// schema browser's preview/lookup statements from the existing, already-working seam, with zero app
/// edits.
/// <para>
/// Mixed scripts are supported additively: a statement starting with <c>db.</c> is tagged
/// <c>"shell"</c> and passed through to MongoDB as-is; everything else is the translated T-SQL
/// surface. See <see cref="MongoScriptClassifier"/>.
/// </para>
/// </summary>
public sealed class MongoTSqlDialect : SqlLanguage, ISqlDialect
{
    private static readonly string[] CompletionKeywords =
    [
        // Core statements.
        "SELECT", "TOP", "DISTINCT", "FROM", "WHERE", "GROUP BY", "HAVING", "ORDER BY", "LIMIT",
        "OFFSET", "INNER JOIN", "LEFT JOIN", "ON", "INSERT INTO", "VALUES", "UPDATE", "SET",
        "DELETE FROM", "USE",
        // Predicates and expressions.
        "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "IN", "BETWEEN", "LIKE", "ILIKE",
        "AS", "ASC", "DESC", "CASE WHEN", "THEN", "ELSE", "END", "CAST", "COUNT", "SUM", "AVG",
        "MIN", "MAX",
        // Mongo shell helpers appear in SQL as functions too.
        "ObjectId", "ISODate", "NumberInt", "NumberLong", "NumberDecimal", "UUID", "Timestamp",
        "MinKey", "MaxKey",
    ];

    private static readonly string[] CompletionFunctions =
    [
        "COUNT", "SUM", "AVG", "MIN", "MAX", "STDEV", "CONCAT", "COALESCE", "ISNULL", "LOWER",
        "UPPER", "LEN", "LENGTH", "ABS", "CEILING", "FLOOR", "ROUND", "SQRT", "POWER", "SUBSTRING",
        "TRIM", "LTRIM", "RTRIM", "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND", "NOW",
        "GETDATE", "CAST", "JSON_VALUE",
        // Mongo shell helpers usable as literals / values.
        "ObjectId", "ISODate", "NumberInt", "NumberLong", "NumberDecimal", "UUID", "Timestamp",
        "MinKey", "MaxKey",
    ];

    private static readonly string[] CompletionTableContext =
    [
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "INTO", "UPDATE", "DELETE FROM",
    ];

    public static MongoTSqlDialect Instance { get; } = new();

    private MongoTSqlDialect()
        : base(new SqlLanguageOptions
        {
            // The T-SQL token keyword set is the correct paint job for the SQL surface; shell
            // statements colour through the same scanner (strings, comments, braces as punctuation).
            TokenKeywords = GenericSqlLanguage.TokenKeywords,
            BracketIdentifiers = true,
            UnicodeStringPrefix = true,
            IdentifierStartCharacters = ['@'],
            IdentifierPartCharacters = ['@', '$'],
            VariablePrefixes = ['@'],
            // Both quotes are strings here; [x] is the identifier quote (handled by BracketIdentifiers).
            StringQuotes = ['\'', '"'],
            LineCommentPrefixes = ["--", "//"],
            BlockCommentOpen = "/*",
            BlockCommentClose = "*/",
            BatchSeparator = null, // No GO: F5 sends the document as one batch; execution splits per statement.
            SplitOnSemicolon = true,
        })
    {
    }

    public override string Id => "mongodb";

    public IReadOnlyList<string> Keywords => CompletionKeywords;

    public IReadOnlyList<string> Functions => CompletionFunctions;

    public IReadOnlyList<string> TableContextKeywords => CompletionTableContext;

    // Mongo has no stored routines; the schema browser and completion should simply offer none.

    /// <summary>
    /// A collection's preview. The row cap is applied by the translator at execution; the statement
    /// text itself carries no TOP so a future collection with more data isn't prematurely clipped.
    /// </summary>
    public string PreviewStatement(string? schema, string name, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(name)}";

    public string RowsStatement(
        string? schema,
        string table,
        IReadOnlyList<RowFilter> filters,
        string? orderByColumn,
        int limit)
    {
        var where = filters.Count == 0
            ? string.Empty
            : " WHERE " + string.Join(
                " AND ",
                filters.Select(filter => filter.Value is null
                    ? $"{QuoteIdentifier(filter.Column)} IS NULL"
                    : $"{QuoteIdentifier(filter.Column)} = {FormatLiteral(filter.Value, filter.DataType)}"));

        var order = string.IsNullOrWhiteSpace(orderByColumn)
            ? string.Empty
            : $" ORDER BY {QuoteIdentifier(orderByColumn)}";

        return $"SELECT * FROM {QuoteIdentifier(table)}{where}{order} LIMIT {Math.Max(1, limit)}";
    }

    public string FormatLiteral(string? value, string? dataType) =>
        value is null ? "NULL" : $"'{value.Replace("'", "''")}'";

    public string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";

    /// <summary>
    /// Reuses the SQL <see cref="SqlLanguage.Split"/> for statement boundaries and then tags each
    /// span as <c>sql</c> or <c>shell</c>, so the editor already knows what is a passthrough document
    /// before execution ever runs.
    /// </summary>
    public override IReadOnlyList<StatementSpan> Split(string? text)
    {
        var spans = base.Split(text);
        if (spans.Count == 0)
        {
            return spans;
        }

        var tagged = new List<StatementSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (text is not null && span.Start >= 0 && span.Length <= text.Length - span.Start)
            {
                tagged.Add(new StatementSpan(
                    span.Start,
                    span.Length,
                    MongoScriptClassifier.Classify(text.Substring(span.Start, span.Length))));
            }
            else
            {
                tagged.Add(span);
            }
        }

        return tagged;
    }
}