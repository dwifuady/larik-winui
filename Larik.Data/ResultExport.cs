using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Larik.Data;

/// <summary>The file formats a result can be written to.</summary>
public enum ResultExportFormat
{
    Csv,
    Json,
}

/// <summary>
/// Formats a <see cref="QueryResult"/> as a file body. Providers already hand the shell display
/// strings, so this needs no type knowledge, does no I/O and works for every provider. Keeping it
/// here (rather than in the WinUI shell) makes CSV/JSON escaping unit-testable without a window.
/// </summary>
public static class ResultExport
{
    public const string CsvExtension = ".csv";

    public const string JsonExtension = ".json";

    public static string Format(QueryResult result, ResultExportFormat format) =>
        format == ResultExportFormat.Json ? ToJson(result) : ToCsv(result);

    /// <summary>
    /// RFC 4180 CSV with a header row and CRLF line endings. NULL is an empty field; an empty
    /// string is quoted so the two stay distinguishable.
    /// </summary>
    public static string ToCsv(QueryResult result)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, result.Columns.Select(column => column.Name));
        foreach (var row in result.Rows)
        {
            AppendCsvRow(builder, row);
        }

        return builder.ToString();
    }

    /// <summary>An array of row objects. NULL becomes <c>null</c>; values stay strings, as in the grid.</summary>
    public static string ToJson(QueryResult result)
    {
        var names = UniqueColumnNames(result.Columns);
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = true,
                // Keep non-ASCII text readable in the file; it is not embedded in HTML.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
        {
            writer.WriteStartArray();
            foreach (var row in result.Rows)
            {
                writer.WriteStartObject();
                for (var index = 0; index < names.Count; index++)
                {
                    var name = names[index];
                    var value = index < row.Length ? row[index] : null;
                    if (value is null)
                    {
                        writer.WriteNull(name);
                    }
                    else
                    {
                        writer.WriteString(name, value);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void AppendCsvRow(StringBuilder builder, IEnumerable<string?> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;

            // A null cell stays empty; an empty string is quoted so a reader can tell them apart.
            if (value is not null)
            {
                builder.Append(EscapeCsv(value));
            }
        }

        builder.Append("\r\n");
    }

    private static string EscapeCsv(string value)
    {
        var needsQuotes =
            value.Length == 0
            || value[0] == ' '
            || value[^1] == ' '
            || value.Contains(',')
            || value.Contains('"')
            || value.Contains('\r')
            || value.Contains('\n');

        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>
    /// JSON objects cannot carry the same key twice, and <c>SELECT id, id</c> is legal SQL, so
    /// later duplicates get a numeric suffix instead of being silently dropped.
    /// </summary>
    private static List<string> UniqueColumnNames(IReadOnlyList<QueryColumn> columns)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(columns.Count);

        foreach (var column in columns)
        {
            var name = string.IsNullOrWhiteSpace(column.Name) ? "column" : column.Name;
            var candidate = name;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{name}_{suffix++}";
            }

            names.Add(candidate);
        }

        return names;
    }
}
