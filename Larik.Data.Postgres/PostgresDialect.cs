using System.Globalization;
using System.Text;
using Larik.Data;

namespace Larik.Data.Postgres;

/// <summary>
/// PostgreSQL as this app's editor understands it: dollar quotes, <c>E'...'</c> escapes, quoted
/// identifiers and <c>LIMIT</c>. Owned by the provider assembly on purpose; the shell has no
/// PostgreSQL keyword, quoting or splitting knowledge of its own.
/// <para>
/// Tokenizing and splitting come from the shared <see cref="SqlLanguage"/> engine; this class only
/// supplies the PostgreSQL configuration and the completion metadata.
/// </para>
/// </summary>
public sealed class PostgresDialect : SqlLanguage, ISqlDialect
{
    /// <summary>
    /// Words the editor paints as keywords. PostgreSQL has a large vocabulary and many words are
    /// context-sensitive, so this is the stable core and common statement words rather than every
    /// catalog keyword.
    /// </summary>
    private static readonly IReadOnlySet<string> PgTokenKeywords = new HashSet<string>(
        new[]
        {
            "ABORT", "ALL", "ALTER", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC",
            "AUTHORIZATION", "BEGIN", "BETWEEN", "BOTH", "BY", "CALL", "CASCADE", "CASE", "CAST",
            "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONCURRENTLY", "CONFLICT", "CONSTRAINT",
            "CREATE", "CROSS", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER",
            "DATABASE", "DEFAULT", "DEFERRABLE", "DELETE", "DESC", "DISTINCT", "DO", "DOMAIN",
            "DROP", "ELSE", "END", "EXCEPT", "EXISTS", "EXPLAIN", "FALSE", "FETCH", "FILTER",
            "FIRST", "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION", "GENERATED", "GRANT", "GROUP",
            "HAVING", "IF", "ILIKE", "IN", "INDEX", "INITIALLY", "INNER", "INSERT", "INTERSECT",
            "INTO", "IS", "ISNULL", "JOIN", "KEY", "LANGUAGE", "LATERAL", "LEADING", "LEFT", "LIKE",
            "LIMIT", "LOCALTIME", "LOCALTIMESTAMP", "MATERIALIZED", "NATURAL", "NOT", "NOTNULL",
            "NULL", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER", "OVER", "OVERLAPS", "PARTITION",
            "PRIMARY", "PROCEDURE", "RANGE", "RECURSIVE", "REFERENCES", "REFRESH", "REINDEX",
            "RETURNING", "RETURNS", "RIGHT", "ROLLBACK", "ROW", "ROWS", "SAVEPOINT", "SCHEMA",
            "SELECT", "SEQUENCE", "SESSION_USER", "SET", "SHOW", "SIMILAR", "SOME", "SYMMETRIC",
            "TABLE", "TABLESAMPLE", "TEMP", "TEMPORARY", "THEN", "TO", "TRAILING", "TRANSACTION",
            "TRIGGER", "TRUE", "TRUNCATE", "TYPE", "UNION", "UNIQUE", "UNLOGGED", "UPDATE", "USER",
            "USING", "VACUUM", "VALUES", "VARIADIC", "VERBOSE", "VIEW", "WHEN", "WHERE", "WINDOW",
            "WITH", "WITHIN", "WITHOUT",
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CompletionKeywords =
    [
        "SELECT", "FROM", "WHERE", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN",
        "LATERAL JOIN", "ON", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "DISTINCT",
        "DISTINCT ON", "AS", "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "IN", "EXISTS",
        "BETWEEN", "LIKE", "ILIKE", "CASE WHEN", "ELSE", "END", "UNION", "UNION ALL", "INTERSECT",
        "EXCEPT", "INSERT INTO", "VALUES", "ON CONFLICT", "RETURNING", "UPDATE", "SET", "DELETE FROM",
        "CREATE TABLE", "CREATE INDEX", "CREATE VIEW", "CREATE FUNCTION", "CREATE PROCEDURE",
        "CREATE SCHEMA", "ALTER TABLE", "DROP TABLE", "TRUNCATE TABLE", "BEGIN", "COMMIT", "ROLLBACK",
        "SAVEPOINT", "CALL", "DO", "EXPLAIN", "ANALYZE", "VACUUM",
        "WITH", "MATERIALIZED", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "ROW_NUMBER",
        "OVER", "PARTITION BY", "CAST", "NOW", "CURRENT_DATE", "CURRENT_TIMESTAMP", "DATE_TRUNC",
        "TO_CHAR", "GENERATE_SERIES", "JSONB_BUILD_OBJECT", "ARRAY_AGG", "STRING_AGG",
    ];

    private static readonly string[] CompletionFunctions =
    [
        "ABS", "AGE", "ARRAY_AGG", "ARRAY_LENGTH", "ARRAY_TO_STRING", "AVG", "BOOL_AND", "BOOL_OR",
        "CARDINALITY", "CEIL", "CEILING", "CHAR_LENGTH", "COALESCE", "CONCAT", "CONCAT_WS",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATE_PART", "DATE_TRUNC", "DECODE",
        "ENCODE", "EXTRACT", "FLOOR", "FORMAT", "GENERATE_SERIES", "GREATEST", "JSON_AGG",
        "JSONB_AGG", "JSONB_BUILD_ARRAY", "JSONB_BUILD_OBJECT", "JSONB_EACH", "JSONB_EACH_TEXT",
        "JSON_BUILD_OBJECT", "LAG", "LEAD", "LEAST", "LEFT", "LENGTH", "LOWER", "LPAD", "LTRIM",
        "MAX", "MD5", "MIN", "NOW", "NULLIF", "PERCENTILE_CONT", "POSITION", "POWER", "RANDOM",
        "RANK", "REGEXP_MATCH", "REGEXP_REPLACE", "REPEAT", "REPLACE", "REVERSE", "RIGHT", "ROUND",
        "ROW_NUMBER", "RPAD", "RTRIM", "SETSEED", "SPLIT_PART", "SQRT", "STRING_AGG", "STRPOS",
        "SUBSTRING", "SUM", "TO_CHAR", "TO_DATE", "TO_JSON", "TO_JSONB", "TO_NUMBER", "TO_TIMESTAMP",
        "TRANSLATE", "TRIM", "UPPER", "UUID_GENERATE_V4", "WIDTH_BUCKET",
    ];

    private static readonly string[] CompletionTableContext =
    [
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "OUTER JOIN",
        "CROSS JOIN", "NATURAL JOIN", "LATERAL", "INTO", "UPDATE", "TABLE", "TRUNCATE", "DELETE FROM",
    ];

    public static PostgresDialect Instance { get; } = new();

    private PostgresDialect()
        : base(new SqlLanguageOptions
        {
            TokenKeywords = PgTokenKeywords,
            BracketIdentifiers = false,
            UnicodeStringPrefix = false,
            // PostgreSQL identifiers start with a letter or underscore; $ is legal inside a name.
            IdentifierStartCharacters = [],
            IdentifierPartCharacters = ['$'],
            VariablePrefixes = [],
            // A double quote is an identifier, not a string: it is skipped but not coloured.
            StringQuotes = ['\''],
            QuotedIdentifierQuotes = ['"'],
            DollarQuotedStrings = true,
            EscapeStringPrefix = true,
            NestedBlockComments = true,
            LineCommentPrefixes = ["--"],
            BlockCommentOpen = "/*",
            BlockCommentClose = "*/",
            BatchSeparator = null,
            SplitOnSemicolon = true,
        })
    {
    }

    public override string Id => "postgres";

    public IReadOnlyList<string> Keywords => CompletionKeywords;

    public IReadOnlyList<string> Functions => CompletionFunctions;

    public IReadOnlyList<string> TableContextKeywords => CompletionTableContext;

    /// <summary>PostgreSQL calls a procedure with CALL; EXEC is not valid here.</summary>
    public IReadOnlyList<string> RoutineContextKeywords => ["CALL"];

    /// <summary>
    /// <c>"public"."refresh_stats"(NULL, NULL)</c>. PostgreSQL has positional call syntax only, so
    /// the parameters become placeholders and the first one is selected for immediate typing.
    /// </summary>
    public RoutineCallText RoutineCall(string? schema, string name, IReadOnlyList<RoutineParameter> parameters)
    {
        var qualified = QualifiedName(schema, name);

        if (parameters.Count == 0)
        {
            return new RoutineCallText($"{qualified}()");
        }

        var builder = new StringBuilder(qualified).Append('(');
        var firstNull = -1;

        for (var index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var nullAt = builder.Length;
            builder.Append("NULL");

            if (firstNull < 0 && !parameters[index].IsOutput)
            {
                firstNull = nullAt;
            }
        }

        builder.Append(')');
        var text = builder.ToString();

        return firstNull < 0
            ? new RoutineCallText(text)
            : new RoutineCallText(text, firstNull, "NULL".Length);
    }

    /// <summary>
    /// PostgreSQL invokes a procedure with <c>CALL</c> and a function as an expression, so the
    /// schema browser inserts <c>SELECT "public"."f"(NULL)</c> for a function. Using CALL for both
    /// would produce a statement the server rejects.
    /// </summary>
    public string RoutineInvocation(
        string? schema,
        string name,
        IReadOnlyList<RoutineParameter> parameters,
        RoutineKind kind)
    {
        var call = RoutineCall(schema, name, parameters).Text;
        return kind == RoutineKind.Function ? $"SELECT {call}" : $"CALL {call}";
    }

    /// <summary><c>SELECT * FROM "public"."orders" LIMIT 100</c>; the schema browser's preview.</summary>
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

        return $"SELECT * FROM {QualifiedName(schema, table)}{where}{order} LIMIT {Math.Max(1, limit)}";
    }

    /// <summary>
    /// PostgreSQL literals: numbers and booleans stay bare, bytea keeps its <c>\x</c> form, and
    /// everything else is a single-quoted string. The grid's values are already provider-formatted,
    /// so this converts back to a literal rather than parsing arbitrary user input.
    /// </summary>
    public string FormatLiteral(string? value, string? dataType)
    {
        if (value is null)
        {
            return "NULL";
        }

        var type = (dataType ?? string.Empty).Split('(', 2)[0].Trim().ToLowerInvariant();
        if (type is "boolean" or "bool")
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "TRUE" : "FALSE";
        }

        if (NumericTypes.Contains(type)
            && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        if (type == "bytea" && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // E'' keeps the bytea \x prefix literal even when standard_conforming_strings is off.
            return $"E'\\\\x{value[2..]}'";
        }

        // E'' makes backslash escaping explicit, so a value containing \' can never close the
        // literal early on a server configured with standard_conforming_strings=off. Doubling the
        // backslashes keeps the value unchanged under both settings.
        return $"E'{value.Replace("\\", "\\\\").Replace("'", "''")}'";
    }

    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "smallint", "int2", "integer", "int", "int4", "bigint", "int8", "decimal", "numeric",
        "real", "float4", "double precision", "float8", "money", "oid",
    };

    public string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    private string QualifiedName(string? schema, string name) =>
        string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";
}
