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
        var tokens = new List<CellSyntaxToken>();
        var index = 0;

        while (index < limit)
        {
            var current = text[index];

            if (current == '"')
            {
                var end = ScanQuoted(text, index, limit, '"', escapes: true);
                var kind = IsFollowedByColon(text, end, limit)
                    ? CellSyntaxTokenKind.Property
                    : CellSyntaxTokenKind.String;
                tokens.Add(new CellSyntaxToken(index, end - index, kind));
                index = end;
                continue;
            }

            if (current == '-' || char.IsAsciiDigit(current))
            {
                var end = ScanNumber(text, index, limit);
                tokens.Add(new CellSyntaxToken(index, end - index, CellSyntaxTokenKind.Number));
                index = end;
                continue;
            }

            if (char.IsAsciiLetter(current))
            {
                var start = index;
                while (index < limit && char.IsAsciiLetter(text[index]))
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

    private static int ScanNumber(string text, int start, int limit)
    {
        var index = start + 1;
        while (index < limit && (char.IsAsciiDigit(text[index]) || text[index] is '.' or 'e' or 'E' or '+' or '-'))
        {
            index++;
        }

        return index;
    }

    private static bool IsFollowedByColon(string text, int index, int limit)
    {
        while (index < limit && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < limit && text[index] == ':';
    }

    private static List<CellSyntaxToken> TokenizeXml(string text, int limit)
    {
        var tokens = new List<CellSyntaxToken>();
        var index = 0;

        while (index < limit)
        {
            var marker = Find(text, index, limit, '<');
            if (marker < 0)
            {
                break;
            }

            index = marker;

            if (Matches(text, index, limit, "<!--"))
            {
                var end = Find(text, index + 4, limit, "-->");
                end = end < 0 ? limit : Math.Min(end + 3, limit);
                tokens.Add(new CellSyntaxToken(index, end - index, CellSyntaxTokenKind.Comment));
                index = end;
                continue;
            }

            if (Matches(text, index, limit, "<![CDATA["))
            {
                var end = Find(text, index + 9, limit, "]]>");
                end = end < 0 ? limit : Math.Min(end + 3, limit);
                tokens.Add(new CellSyntaxToken(index, end - index, CellSyntaxTokenKind.String));
                index = end;
                continue;
            }

            if (Matches(text, index, limit, "<!") || Matches(text, index, limit, "<?"))
            {
                var end = Find(text, index + 2, limit, '>');
                end = end < 0 ? limit : end + 1;
                tokens.Add(new CellSyntaxToken(index, end - index, CellSyntaxTokenKind.Declaration));
                index = end;
                continue;
            }

            index = ScanTag(text, index, limit, tokens);
        }

        return tokens;
    }

    /// <summary>
    /// Emits the punctuation, element name, attribute names and quoted attribute values of one tag.
    /// Returns the index just past the closing <c>&gt;</c>, so a malformed tag cannot loop forever.
    /// </summary>
    private static int ScanTag(string text, int start, int limit, List<CellSyntaxToken> tokens)
    {
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
                cursor = ScanQuoted(text, cursor, limit, current, escapes: false);
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

    private static int ScanQuoted(string text, int start, int limit, char quote, bool escapes)
    {
        var index = start + 1;
        while (index < limit)
        {
            if (escapes && text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == quote)
            {
                return index + 1;
            }

            index++;
        }

        return limit;
    }

    private static int Find(string text, int start, int limit, char value)
    {
        for (var index = start; index < limit; index++)
        {
            if (text[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private static int Find(string text, int start, int limit, string value)
    {
        for (var index = start; index + value.Length <= limit; index++)
        {
            if (text.AsSpan(index, value.Length).SequenceEqual(value))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Matches(string text, int start, int limit, string value) =>
        start + value.Length <= limit && text.AsSpan(start, value.Length).SequenceEqual(value);
}
