using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Larik.Data;

/// <summary>How a cell's text should be laid out in the preview panel.</summary>
public enum CellFormat
{
    /// <summary>Detect JSON or XML and fall back to plain text.</summary>
    Auto,
    Json,
    Xml,
    Plain,
}

/// <summary>
/// The text to display plus what it was treated as. <see cref="Error"/> is set when an explicit
/// format could not be applied; the text is then the raw value, so nothing is ever hidden.
/// </summary>
public readonly record struct FormattedCellContent(string Text, CellFormat Kind, string? Error);

/// <summary>
/// Pretty-prints JSON and XML cell values for the results preview. Pure and dependency-free so the
/// formatting rules are unit-tested away from the window; providers already hand the grid strings,
/// so no type knowledge is needed.
/// </summary>
public static partial class CellContentFormatter
{
    /// <summary>Formatting is pure allocation; a multi-megabyte cell stays raw.</summary>
    public const int MaxFormatLength = 1_000_000;

    private static readonly JsonWriterOptions JsonWriterOpts = new()
    {
        Indented = true,
        // Deterministic line endings: the preview and its copy should not vary by platform.
        NewLine = "\n",
        // Keep non-ASCII text readable in the preview; it is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Safe because Utf8JsonReader validates the token stream before every write.
        SkipValidation = true,
    };

    public static FormattedCellContent Format(string? value, CellFormat format)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new FormattedCellContent(value ?? string.Empty, CellFormat.Plain, null);
        }

        if (value.Length > MaxFormatLength)
        {
            return new FormattedCellContent(
                value,
                CellFormat.Plain,
                format == CellFormat.Auto ? null : "Too large to format, showing raw text");
        }

        if (format == CellFormat.Auto)
        {
            var trimmed = value.AsSpan().Trim();
            if (trimmed.Length == 0)
            {
                return new FormattedCellContent(value, CellFormat.Plain, null);
            }

            // Parse JSON once in Auto mode so we never pay JsonDocument.Parse twice.
            if (trimmed[0] is '{' or '[' && TryFormatJson(value, out var jsonFormatted))
            {
                return new FormattedCellContent(jsonFormatted, CellFormat.Json, null);
            }

            if (LooksLikeXml(trimmed))
            {
                return FormatXmlValidated(trimmed);
            }

            return new FormattedCellContent(value, CellFormat.Plain, null);
        }

        return format switch
        {
            CellFormat.Json => FormatJson(value),
            CellFormat.Xml => FormatXml(value),
            _ => new FormattedCellContent(value, CellFormat.Plain, null),
        };
    }

    /// <summary>NULL/empty cells are plain; JSON wins over XML, exactly like the desktop app.</summary>
    public static CellFormat Detect(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length == 0)
        {
            return CellFormat.Plain;
        }

        if ((trimmed[0] == '{' || trimmed[0] == '[') && TryParseJson(value))
        {
            return CellFormat.Json;
        }

        return LooksLikeXml(trimmed) ? CellFormat.Xml : CellFormat.Plain;
    }

    private static bool TryParseJson(string value)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(value, rented);
            var reader = new Utf8JsonReader(rented.AsSpan(0, byteCount));
            var hadToken = false;
            while (reader.Read())
            {
                hadToken = true;
            }

            return hadToken;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryFormatJson(string value, out string formatted)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(value, rented);
            using var buffer = new PooledByteWriter(Math.Clamp(byteCount * 2, 256, 2 * 1024 * 1024));
            var hadToken = false;

            using (var writer = new Utf8JsonWriter(buffer, JsonWriterOpts))
            {
                var reader = new Utf8JsonReader(rented.AsSpan(0, byteCount));
                while (reader.Read())
                {
                    hadToken = true;
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            writer.WriteStartObject();
                            break;
                        case JsonTokenType.EndObject:
                            writer.WriteEndObject();
                            break;
                        case JsonTokenType.StartArray:
                            writer.WriteStartArray();
                            break;
                        case JsonTokenType.EndArray:
                            writer.WriteEndArray();
                            break;
                        case JsonTokenType.PropertyName:
                            if (!reader.ValueIsEscaped)
                            {
                                writer.WritePropertyName(reader.ValueSpan);
                            }
                            else
                            {
                                writer.WritePropertyName(reader.GetString()!);
                            }
                            break;
                        case JsonTokenType.String:
                            if (!reader.ValueIsEscaped)
                            {
                                writer.WriteStringValue(reader.ValueSpan);
                            }
                            else
                            {
                                writer.WriteStringValue(reader.GetString());
                            }
                            break;
                        case JsonTokenType.Number:
                            if (reader.TryGetInt64(out var intValue))
                            {
                                writer.WriteNumberValue(intValue);
                            }
                            else if (reader.TryGetDecimal(out var decimalValue))
                            {
                                writer.WriteNumberValue(decimalValue);
                            }
                            else
                            {
                                writer.WriteNumberValue(reader.GetDouble());
                            }
                            break;
                        case JsonTokenType.True:
                            writer.WriteBooleanValue(true);
                            break;
                        case JsonTokenType.False:
                            writer.WriteBooleanValue(false);
                            break;
                        case JsonTokenType.Null:
                            writer.WriteNullValue();
                            break;
                    }
                }
            }

            if (!hadToken)
            {
                formatted = string.Empty;
                return false;
            }

            formatted = Encoding.UTF8.GetString(buffer.WrittenSpan);
            return true;
        }
        catch (JsonException)
        {
            formatted = string.Empty;
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static FormattedCellContent FormatJson(string value) =>
        TryFormatJson(value, out var formatted)
            ? new FormattedCellContent(formatted, CellFormat.Json, null)
            : new FormattedCellContent(value, CellFormat.Json, "Invalid JSON, showing raw text");

    private static bool LooksLikeXml(ReadOnlySpan<char> value)
    {
        if (value.Length < 4 || value[0] != '<')
        {
            return false;
        }

        var cursor = 0;
        while (cursor < value.Length)
        {
            var lt = value.Slice(cursor).IndexOf('<');
            if (lt < 0)
            {
                return false;
            }

            cursor += lt;
            if (TryReadOpenTagName(value.Slice(cursor), out var tagName))
            {
                return ContainsClosingTag(value, tagName);
            }

            cursor++;
        }

        return false;
    }

    /// <summary>Matches <c>&lt;([^/?!\s&gt;]+)[^&gt;]*&gt;</c> at index 0 of <paramref name="span"/>.</summary>
    private static bool TryReadOpenTagName(ReadOnlySpan<char> span, out ReadOnlySpan<char> name)
    {
        name = default;
        if (span.Length < 3 || span[0] != '<')
        {
            return false;
        }

        var i = 1;
        while (i < span.Length)
        {
            var ch = span[i];
            if (ch is '/' or '?' or '!' or '>' || char.IsWhiteSpace(ch))
            {
                break;
            }

            i++;
        }

        if (i == 1)
        {
            return false;
        }

        var closeOffset = span.Slice(i).IndexOf('>');
        if (closeOffset < 0)
        {
            return false;
        }

        name = span.Slice(1, i - 1);
        return true;
    }

    private static bool ContainsClosingTag(ReadOnlySpan<char> value, ReadOnlySpan<char> tagName)
    {
        var cursor = 0;
        var neededAfterPrefix = tagName.Length + 1;
        while (cursor < value.Length)
        {
            var offset = value.Slice(cursor).IndexOf("</", StringComparison.Ordinal);
            if (offset < 0)
            {
                return false;
            }

            var nameStart = cursor + offset + 2;
            if (nameStart + neededAfterPrefix <= value.Length
                && value[nameStart + tagName.Length] == '>'
                && value.Slice(nameStart, tagName.Length).SequenceEqual(tagName))
            {
                return true;
            }

            cursor = nameStart;
        }

        return false;
    }

    private static bool EndsWithClosingTag(ReadOnlySpan<char> line, ReadOnlySpan<char> tagName)
    {
        var suffixLength = tagName.Length + 3; // "</" + name + ">"
        return line.Length >= suffixLength
            && line[^1] == '>'
            && line[line.Length - suffixLength] == '<'
            && line[line.Length - suffixLength + 1] == '/'
            && line.Slice(line.Length - tagName.Length - 1, tagName.Length).SequenceEqual(tagName);
    }

    /// <summary>
    /// Mirrors the desktop app's lightweight XML pass: a newline at every <c>&gt;&lt;</c> boundary,
    /// then two spaces per nesting level. It is deliberately not a parser, so malformed documents
    /// degrade to a readable layout instead of failing.
    /// </summary>
    private static FormattedCellContent FormatXml(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (!LooksLikeXml(trimmed))
        {
            return new FormattedCellContent(value, CellFormat.Xml, "Not XML, showing raw text");
        }

        return FormatXmlValidated(trimmed);
    }

    private static FormattedCellContent FormatXmlValidated(ReadOnlySpan<char> trimmed)
    {
        var builder = new StringBuilder(trimmed.Length + 128);
        var indent = 0;
        var segmentStart = 0;
        var cursor = 0;

        while (cursor < trimmed.Length)
        {
            var next = trimmed.Slice(cursor).IndexOfAny('>', '\n');
            if (next < 0)
            {
                break;
            }

            cursor += next;
            if (trimmed[cursor] == '\n')
            {
                ProcessXmlLine(builder, trimmed.Slice(segmentStart, cursor - segmentStart), ref indent);
                cursor++;
                segmentStart = cursor;
                continue;
            }

            // Check for >\s*< boundary: split keeping '>' on the current line and '<' on the next.
            var afterGt = cursor + 1;
            var probe = afterGt;
            while (probe < trimmed.Length && char.IsWhiteSpace(trimmed[probe]))
            {
                probe++;
            }

            if (probe < trimmed.Length && trimmed[probe] == '<')
            {
                ProcessXmlLine(builder, trimmed.Slice(segmentStart, afterGt - segmentStart), ref indent);
                segmentStart = probe;
                cursor = probe;
            }
            else
            {
                cursor = afterGt;
            }
        }

        if (segmentStart < trimmed.Length)
        {
            ProcessXmlLine(builder, trimmed.Slice(segmentStart), ref indent);
        }

        while (builder.Length > 0 && builder[^1] == '\n')
        {
            builder.Length--;
        }

        return new FormattedCellContent(builder.ToString(), CellFormat.Xml, null);
    }

    private static void ProcessXmlLine(StringBuilder builder, ReadOnlySpan<char> rawLine, ref int indent)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return;
        }

        if (line.StartsWith("</", StringComparison.Ordinal))
        {
            indent = Math.Max(0, indent - 1);
            Append(builder, indent, line);
            return;
        }

        if (line.EndsWith("/>", StringComparison.Ordinal))
        {
            Append(builder, indent, line);
            return;
        }

        if (TryReadOpenTagName(line, out var name))
        {
            if (EndsWithClosingTag(line, name))
            {
                Append(builder, indent, line);
                return;
            }

            Append(builder, indent, line);
            indent++;
            return;
        }

        Append(builder, indent, line);
    }

    private static void Append(StringBuilder builder, int indent, ReadOnlySpan<char> line) =>
        builder.Append(' ', indent * 2).Append(line).Append('\n');

    private sealed class PooledByteWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable
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
}
