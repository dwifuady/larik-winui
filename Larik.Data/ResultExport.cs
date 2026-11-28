using System.Buffers;
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
    private static readonly SearchValues<char> CsvSpecialChars = SearchValues.Create([',', '"', '\r', '\n']);

    private static readonly JsonWriterOptions ExportJsonWriterOptions = new()
    {
        Indented = true,
        // Keep non-ASCII text readable in the file; it is not embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
        var columnCount = result.Columns.Count;
        var rowCount = result.Rows.Count;
        var estimatedChars = Math.Clamp((rowCount + 1) * Math.Max(columnCount, 1) * 16, 256, 16 * 1024 * 1024);
        var builder = new StringBuilder(estimatedChars);

        for (var c = 0; c < columnCount; c++)
        {
            if (c > 0)
            {
                builder.Append(',');
            }

            AppendCsvCell(builder, result.Columns[c].Name);
        }

        builder.Append("\r\n");

        for (var r = 0; r < rowCount; r++)
        {
            var row = result.Rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                if (c > 0)
                {
                    builder.Append(',');
                }

                // A null cell stays empty; an empty string is quoted so a reader can tell them apart.
                var value = row[c];
                if (value is not null)
                {
                    AppendCsvCell(builder, value);
                }
            }

            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>An array of row objects. NULL becomes <c>null</c>; values stay strings, as in the grid.</summary>
    public static string ToJson(QueryResult result)
    {
        var names = UniqueColumnNames(result.Columns);
        var encodedNames = new JsonEncodedText[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            encodedNames[i] = JsonEncodedText.Encode(names[i], JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        }

        var estimatedBytes = Math.Clamp(result.Rows.Count * Math.Max(encodedNames.Length, 1) * 32, 1024, 16 * 1024 * 1024);
        using var buffer = new PooledByteBufferWriter(estimatedBytes);

        using (var writer = new Utf8JsonWriter(buffer, ExportJsonWriterOptions))
        {
            writer.WriteStartArray();
            var rows = result.Rows;
            for (var r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                writer.WriteStartObject();
                for (var index = 0; index < encodedNames.Length; index++)
                {
                    var name = encodedNames[index];
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

        // Append the trailing LF directly to the UTF-8 buffer so we decode once with no string concat copy.
        var trailing = buffer.GetSpan(1);
        trailing[0] = (byte)'\n';
        buffer.Advance(1);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class PooledByteBufferWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            var required = Math.Max(sizeHint, 1);
            if (_buffer.Length - _written >= required)
            {
                return;
            }

            var newSize = Math.Max(_buffer.Length * 2, _written + required);
            var next = ArrayPool<byte>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _written).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = [];
            if (buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Makes a value inert when a spreadsheet opens it. Excel and LibreOffice treat a cell that
    /// starts with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> (or a control character) as a formula,
    /// and formulas can fetch remote content (<c>HYPERLINK</c>, <c>WEBSERVICE</c>). A leading
    /// apostrophe is the spreadsheet's own "this is text" marker and is not shown in the cell.
    /// JSON keeps the original value: only tabular formats can carry a live formula.
    /// </summary>
    public static string NeutralizeSpreadsheetFormula(string value) =>
        NeedsFormulaNeutralization(value) ? "'" + value : value;

    private static bool NeedsFormulaNeutralization(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';

    private static void AppendCsvCell(StringBuilder builder, string value)
    {
        var neutralize = NeedsFormulaNeutralization(value);
        var span = value.AsSpan();
        var needsQuotes =
            span.Length == 0
            || (!neutralize && span[0] == ' ')
            || span[^1] == ' '
            || span.IndexOfAny(CsvSpecialChars) >= 0;

        if (!needsQuotes)
        {
            if (neutralize)
            {
                builder.Append('\'');
            }

            builder.Append(value);
            return;
        }

        builder.Append('"');
        if (neutralize)
        {
            builder.Append('\'');
        }

        var cursor = 0;
        while (cursor < span.Length)
        {
            var quoteOffset = span.Slice(cursor).IndexOf('"');
            if (quoteOffset < 0)
            {
                builder.Append(span.Slice(cursor));
                break;
            }

            if (quoteOffset > 0)
            {
                builder.Append(span.Slice(cursor, quoteOffset));
            }

            builder.Append("\"\"");
            cursor += quoteOffset + 1;
        }

        builder.Append('"');
    }

    /// <summary>
    /// JSON objects cannot carry the same key twice, and <c>SELECT id, id</c> is legal SQL, so
    /// later duplicates get a numeric suffix instead of being silently dropped.
    /// </summary>
    private static List<string> UniqueColumnNames(IReadOnlyList<QueryColumn> columns)
    {
        var used = new HashSet<string>(columns.Count, StringComparer.OrdinalIgnoreCase);
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
