using System.Globalization;
using System.Text;
using Larik.Data;

namespace Larik.Data.SqlServer;

/// <summary>
/// T-SQL as this app's editor has always understood it. Owned by the provider assembly on purpose:
/// the shell has no T-SQL keyword, quoting or <c>GO</c> knowledge of its own.
/// <para>
/// Tokenizing and splitting come from the shared <see cref="SqlLanguage"/> engine; this class only
/// supplies the T-SQL configuration and the completion metadata.
/// </para>
/// </summary>
public sealed class TSqlDialect : SqlLanguage, ISqlDialect
{
    public static TSqlDialect Instance { get; } = new();

    private static readonly string[] CompletionKeywords =
    [
        "SELECT", "FROM", "JOIN", "WHERE", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "ON",
        "GROUP BY", "ORDER BY", "HAVING", "TOP", "DISTINCT", "AS", "AND", "OR", "NOT", "NULL", "IS NULL",
        "IN", "EXISTS", "BETWEEN", "LIKE", "CASE WHEN", "ELSE", "END", "UNION", "UNION ALL", "INSERT INTO",
        "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "TRUNCATE TABLE",
        "BEGIN TRANSACTION", "COMMIT", "ROLLBACK", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "ROW_NUMBER",
        "OVER", "PARTITION BY", "CAST", "GETDATE", "SYSDATETIME", "DATEADD", "DATEDIFF", "FORMAT",
    ];

    private static readonly string[] CompletionFunctions =
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

    private static readonly string[] CompletionTableContext =
    [
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "OUTER JOIN",
        "CROSS JOIN", "APPLY", "CROSS APPLY", "OUTER APPLY", "INTO", "UPDATE", "TABLE",
    ];

    private TSqlDialect()
        : base(new SqlLanguageOptions
        {
            TokenKeywords = GenericSqlLanguage.TokenKeywords,
            BracketIdentifiers = true,
            UnicodeStringPrefix = true,
            IdentifierStartCharacters = ['@', '#'],
            IdentifierPartCharacters = ['@', '#', '$'],
            VariablePrefixes = ['@'],
            StringQuotes = ['\'', '"'],
            LineCommentPrefixes = ["--"],
            BlockCommentOpen = "/*",
            BlockCommentClose = "*/",
            BatchSeparator = "GO",
            SplitOnSemicolon = true,
        })
    {
    }

    public override string Id => "tsql";

    public IReadOnlyList<string> Keywords => CompletionKeywords;

    public IReadOnlyList<string> Functions => CompletionFunctions;

    public IReadOnlyList<string> TableContextKeywords => CompletionTableContext;

    /// <summary>T-SQL calls a procedure with EXEC; CALL is not valid here.</summary>
    public IReadOnlyList<string> RoutineContextKeywords => ["EXEC", "EXECUTE"];

    /// <summary>
    /// <c>[dbo].[p] @a = NULL, @b = NULL</c>. Positional syntax is rarer and breaks when a
    /// procedure gains a parameter; named arguments stay valid and read like SSMS scripts.
    /// The first value is returned as the selection so the user types over it immediately.
    /// </summary>
    public RoutineCallText RoutineCall(string? schema, string name, IReadOnlyList<RoutineParameter> parameters)
    {
        var qualified = QualifiedName(schema, name);

        if (parameters.Count == 0)
        {
            return new RoutineCallText(qualified);
        }

        var builder = new StringBuilder(qualified);
        var firstNull = -1;
        var first = true;

        foreach (var parameter in parameters)
        {
            builder.Append(first ? " " : ", ").Append(parameter.Name).Append(" = ");
            first = false;

            var nullAt = builder.Length;
            builder.Append("NULL");

            if (firstNull < 0 && !parameter.IsOutput)
            {
                firstNull = nullAt;
            }
        }

        var text = builder.ToString();

        return firstNull < 0
            ? new RoutineCallText(text)
            : new RoutineCallText(text, firstNull, "NULL".Length);
    }

    /// <summary><c>SELECT TOP 100 * FROM [dbo].[Orders]</c>; the schema browser's preview.</summary>
    public string PreviewStatement(string? schema, string name, int limit) =>
        RowsStatement(schema, name, [], orderByColumn: null, limit);

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

        return $"SELECT TOP ({Math.Max(1, limit)}) * FROM {QualifiedName(schema, table)}{where}{order}";
    }

    /// <summary>
    /// T-SQL literals: numbers/bit stay bare, binary values keep their <c>0x</c> form, everything
    /// else is an escaped Unicode string. The grid's values are already provider-formatted, so this
    /// is a conversion back to a literal, not a parser for arbitrary user input.
    /// </summary>
    public string FormatLiteral(string? value, string? dataType)
    {
        if (value is null)
        {
            return "NULL";
        }

        var type = (dataType ?? string.Empty).Split('(', 2)[0].Trim().ToLowerInvariant();
        if (type == "bit")
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? "1"
                : value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? "0"
                    : value;
        }

        if (NumericTypes.Contains(type)
            && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        if (BinaryTypes.Contains(type))
        {
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value : $"0x{value}";
        }

        return $"N'{value.Replace("'", "''")}'";
    }

    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney",
        "float", "real",
    };

    private static readonly HashSet<string> BinaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "binary", "varbinary", "image", "timestamp", "rowversion",
    };

    public string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";

    private string QualifiedName(string? schema, string name) =>
        string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";
}
