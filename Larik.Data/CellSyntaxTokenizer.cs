namespace Larik.Data;

/// <summary>
/// Lexical classes the cell preview colours for JSON and XML. Deliberately small: the preview is a
/// reading surface, so it only distinguishes what changes the shape of the document (keys, values,
/// tags, attributes, comments) from the punctuation between them.
/// </summary>
public enum CellSyntaxTokenKind
{
    Property,
    String,
    Number,
    Keyword,
    Punctuation,
    Tag,
    Attribute,
    Comment,
    Declaration,
}

/// <summary>A classified range in the preview's text coordinates.</summary>
public readonly record struct CellSyntaxToken(int Start, int Length, CellSyntaxTokenKind Kind)
{
    public int End => Start + Length;
}

/// <summary>
/// A tiny, allocation-conscious tokenizer for the JSON and XML that <see cref="CellContentFormatter"/>
/// pretty-prints. Ranges between tokens are whitespace and stay plain. It is not a validator:
/// malformed input simply yields fewer colours, never an error or hidden text.
/// </summary>
public static class CellSyntaxTokenizer
{
    /// <summary>
    /// Classifies <paramref name="text"/> up to <paramref name="limit"/> characters. The pane caps
    /// the painted span because one <c>Run</c> per token is the entire cost of colour, and no one
    /// reads a 100,000-character cell by eye. A token that straddles the limit is kept whole.
    /// </summary>
    public static IReadOnlyList<CellSyntaxToken> Tokenize(
        string? text,
        CellFormat format,
        int limit = int.MaxValue)
    {
        if (string.IsNullOrEmpty(text) || limit <= 0)
        {
            return [];
        }

        var end = Math.Min(limit, text.Length);
        return format switch
        {
            CellFormat.Json => TokenizeJson(text, end),
            CellFormat.Xml => TokenizeXml(text, end),
            _ => [],
        };
    }

    private static List<CellSyntaxToken> TokenizeJson(string text, int limit)
    {
        var span = text.AsSpan(0, limit);
        var tokens = new List<CellSyntaxToken>(Math.Clamp(limit / 8, 16, 4096));
        var index = 0;

        while (index < limit)
        {
            var current = span[index];

            if (current == '"')
            {
                var end = ScanQuoted(span, index, '"', escapes: true);
                var kind = IsFollowedByColon(span, end)
                    ? CellSyntaxTokenKind.Property
                    : CellSyntaxTokenKind.String;
                tokens.Add(new CellSyntaxToken(index, end - index, kind));
                index = end;
                continue;
            }

            if (current == '-' || char.IsAsciiDigit(current))
            {
                var end = ScanNumber(span, index);
                tokens.Add(new CellSyntaxToken(index, end - index, CellSyntaxTokenKind.Number));
                index = end;
                continue;
            }

            if (char.IsAsciiLetter(current))
            {
                var start = index;
                while (index < limit && char.IsAsciiLetter(span[index]))
                {
                    index++;
                }

                tokens.Add(new CellSyntaxToken(start, index - start, CellSyntaxTokenKind.Keyword));
                continue;
            }

            if (current is '{' or '}' or '[' or ']' or ',' or ':')
            {
                tokens.Add(new CellSyntaxToken(index, 1, CellSyntaxTokenKind.Punctuation));
            }

            index++;
        }

        return tokens;
    }

    private static int ScanNumber(ReadOnlySpan<char> text, int start)
    {
        var index = start + 1;
        while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] is '.' or 'e' or 'E' or '+' or '-'))
        {
            index++;
        }

        return index;
    }

    private static bool IsFollowedByColon(ReadOnlySpan<char> text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length && text[index] == ':';
    }

    private static List<CellSyntaxToken> TokenizeXml(string text, int limit)
    {
        var span = text.AsSpan(0, limit);
        var tokens = new List<CellSyntaxToken>(Math.Clamp(limit / 8, 16, 4096));
        var index = 0;

        while (index < limit)
        {
            var markerOffset = span.Slice(index).IndexOf('<');
            if (markerOffset < 0)
            {
                break;
            }

            index += markerOffset;

            if (span.Slice(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var endOffset = span.Slice(index + 4).IndexOf("-->", StringComparison.Ordinal);
                var end = endOffset < 0 ? limit : Math.Min(index + 4 + endOffset + 3, limit);
                tokens.Add(new CellSyntaxToken(index, end - index, CellSyntaxTokenKind.Comment));
                index = end;
                continue;
            }

            if (span.Slice(index).StartsWith("<![CDATA[", StringComparison.Ordinal))
            {
                var endOffset = span.Slice(index + 9).IndexOf("]]>", StringComparison.Ordinal);
                var end = endOffset < 0 ? limit : Math.Min(index + 9 + endOffset + 3, limit);
                tokens.Add(new CellSyntaxToken(index, end - index, CellSyntaxTokenKind.String));
                index = end;
                continue;
            }

            if (index + 1 < limit && span[index + 1] is '!' or '?')
            {
                var endOffset = span.Slice(index + 2).IndexOf('>');
                var end = endOffset < 0 ? limit : index + 2 + endOffset + 1;
                tokens.Add(new CellSyntaxToken(index, end - index, CellSyntaxTokenKind.Declaration));
                index = end;
                continue;
            }

            index = ScanTag(span, index, tokens);
        }

        return tokens;
    }

    /// <summary>
    /// Emits the punctuation, element name, attribute names and quoted attribute values of one tag.
    /// Returns the index just past the closing <c>&gt;</c>, so a malformed tag cannot loop forever.
    /// </summary>
    private static int ScanTag(ReadOnlySpan<char> text, int start, List<CellSyntaxToken> tokens)
    {
        var limit = text.Length;
        var cursor = start;
        tokens.Add(new CellSyntaxToken(cursor++, 1, CellSyntaxTokenKind.Punctuation));

        if (cursor < limit && text[cursor] == '/')
        {
            tokens.Add(new CellSyntaxToken(cursor++, 1, CellSyntaxTokenKind.Punctuation));
        }

        var nameStart = cursor;
        while (cursor < limit && !char.IsWhiteSpace(text[cursor]) && text[cursor] is not ('>' or '/'))
        {
            cursor++;
        }

        if (cursor > nameStart)
        {
            tokens.Add(new CellSyntaxToken(nameStart, cursor - nameStart, CellSyntaxTokenKind.Tag));
        }

        while (cursor < limit)
        {
            var current = text[cursor];

            if (char.IsWhiteSpace(current))
            {
                cursor++;
                continue;
            }

            if (current is '>' or '/' or '=')
            {
                tokens.Add(new CellSyntaxToken(cursor++, 1, CellSyntaxTokenKind.Punctuation));
                if (current == '>')
                {
                    break;
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                var startQuote = cursor;
                cursor = ScanQuoted(text, cursor, current, escapes: false);
                tokens.Add(new CellSyntaxToken(startQuote, cursor - startQuote, CellSyntaxTokenKind.String));
                continue;
            }

            var attributeStart = cursor;
            while (cursor < limit
                && !char.IsWhiteSpace(text[cursor])
                && text[cursor] is not ('=' or '>' or '/'))
            {
                cursor++;
            }

            if (cursor > attributeStart)
            {
                tokens.Add(new CellSyntaxToken(attributeStart, cursor - attributeStart, CellSyntaxTokenKind.Attribute));
            }
            else
            {
                // A character none of the branches above accepted (e.g. a stray '!'); skip it.
                cursor++;
            }
        }

        return cursor;
    }

    private static int ScanQuoted(ReadOnlySpan<char> text, int start, char quote, bool escapes)
    {
        var index = start + 1;
        if (!escapes)
        {
            var closeOffset = text.Slice(index).IndexOf(quote);
            return closeOffset < 0 ? text.Length : index + closeOffset + 1;
        }

        while (index < text.Length)
        {
            var offset = text.Slice(index).IndexOfAny(quote, '\\');
            if (offset < 0)
            {
                return text.Length;
            }

            index += offset;
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return text.Length;
    }
}
