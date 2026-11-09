using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Deterministic line endings: the preview and its copy should not vary by platform.
        NewLine = "\n",
        // Keep non-ASCII text readable in the preview; it is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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

        var actual = format == CellFormat.Auto ? Detect(value) : format;
        return actual switch
        {
            CellFormat.Json => FormatJson(value),
            CellFormat.Xml => FormatXml(value),
            _ => new FormattedCellContent(value, CellFormat.Plain, null),
        };
    }

    /// <summary>NULL/empty cells are plain; JSON wins over XML, exactly like the desktop app.</summary>
    public static CellFormat Detect(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return CellFormat.Plain;
        }

        if ((trimmed[0] == '{' || trimmed[0] == '[') && TryParseJson(trimmed))
        {
            return CellFormat.Json;
        }

        return LooksLikeXml(trimmed) ? CellFormat.Xml : CellFormat.Plain;
    }

    private static bool TryParseJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeXml(string value) =>
        value.StartsWith('<')
        && OpenTagAnywhere().Match(value) is { Success: true } match
        && value.Contains($"</{match.Groups[1].Value}>", StringComparison.Ordinal);

    private static FormattedCellContent FormatJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return new FormattedCellContent(
                JsonSerializer.Serialize(document.RootElement, JsonOptions),
                CellFormat.Json,
                null);
        }
        catch (JsonException)
        {
            return new FormattedCellContent(value, CellFormat.Json, "Invalid JSON, showing raw text");
        }
    }

    /// <summary>
    /// Mirrors the desktop app's lightweight XML pass: a newline at every <c>&gt;&lt;</c> boundary,
    /// then two spaces per nesting level. It is deliberately not a parser, so malformed documents
    /// degrade to a readable layout instead of failing.
    /// </summary>
    private static FormattedCellContent FormatXml(string value)
    {
        if (!LooksLikeXml(value.Trim()))
        {
            return new FormattedCellContent(value, CellFormat.Xml, "Not XML, showing raw text");
        }

        var normalized = TagBoundary().Replace(value.Trim(), ">\n<");
        var builder = new StringBuilder(value.Length + 128);
        var indent = 0;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("</", StringComparison.Ordinal))
            {
                indent = Math.Max(0, indent - 1);
                Append(builder, indent, line);
                continue;
            }

            if (line.EndsWith("/>", StringComparison.Ordinal))
            {
                Append(builder, indent, line);
                continue;
            }

            var open = OpenTagAtStart().Match(line);
            if (open.Success)
            {
                var name = open.Groups[1].Value;
                if (line.EndsWith($"</{name}>", StringComparison.Ordinal))
                {
                    Append(builder, indent, line);
                    continue;
                }

                Append(builder, indent, line);
                indent++;
                continue;
            }

            Append(builder, indent, line);
        }

        return new FormattedCellContent(builder.ToString().TrimEnd('\n'), CellFormat.Xml, null);
    }

    private static void Append(StringBuilder builder, int indent, string line) =>
        builder.Append(' ', indent * 2).Append(line).Append('\n');

    [GeneratedRegex(@">\s*<")]
    private static partial Regex TagBoundary();

    [GeneratedRegex(@"<([^/?!\s>]+)[^>]*>")]
    private static partial Regex OpenTagAnywhere();

    [GeneratedRegex(@"^<([^/?!\s>]+)[^>]*>")]
    private static partial Regex OpenTagAtStart();
}
