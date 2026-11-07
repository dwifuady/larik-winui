using Larik.Editor;

namespace Larik.Services;

/// <summary>
/// Finds the statement a caret belongs to. Editors and providers split batches differently
/// (SQL Server executes natively, the demo provider splits on semicolons), so this is the one
/// client-side splitter used for "run the current statement" and selection highlighting.
/// <para>
/// It understands the lexical boundaries that matter for SQL: single-quoted strings (with
/// doubled quotes), double-quoted strings/identifiers, bracketed identifiers, line comments,
/// block comments and T-SQL <c>GO</c> batch separators.
/// </para>
/// </summary>
public static class SqlStatementParser
{
    /// <summary>
    /// Returns the trimmed span of the statement containing <paramref name="caretOffset"/>.
    /// A caret in the whitespace after a statement belongs to that statement, which is what
    /// makes <c>Ctrl+Enter</c> behave like SSMS at the end of a line. An empty document (or one
    /// with no complete statement) returns the trimmed whole document.
    /// </summary>
    public static EditorSpan FindStatement(string? text, int caretOffset)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new EditorSpan(0, 0);
        }

        var statements = Split(text);
        if (statements.Count == 0)
        {
            var (start, length) = TrimSpan(text, 0, text.Length);
            return HasCode(text, start, length) ? new EditorSpan(start, length) : new EditorSpan(0, 0);
        }

        var caret = Math.Clamp(caretOffset, 0, text.Length);

        // A caret after the last statement's end (typically the final semicolon or trailing
        // newline) still means that last statement.
        var first = statements[0];
        if (caret <= first.Start)
        {
            return first;
        }

        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];
            if (caret <= statement.End)
            {
                return statement;
            }

            // Between two statements the caret sits in separator whitespace: prefer the
            // statement it follows, so pressing Ctrl+Enter on a blank line repeats the
            // statement the user just looked at.
            if (index + 1 < statements.Count && caret < statements[index + 1].Start)
            {
                return statement;
            }
        }

        return statements[^1];
    }

    /// <summary>Spans of every non-empty statement with executable content, in document order.</summary>
    public static IReadOnlyList<EditorSpan> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var statements = new List<EditorSpan>();
        var statementStart = 0;
        var index = 0;

        void Emit(int end)
        {
            var (start, length) = TrimSpan(text, statementStart, end);
            if (length > 0 && HasCode(text, start, length))
            {
                statements.Add(new EditorSpan(start, length));
            }
        }

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\'' || current == '"')
            {
                index = SkipQuoted(text, index, current);
                continue;
            }

            if (current == '[')
            {
                index = SkipBracket(text, index);
                continue;
            }

            if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                index = SkipLineComment(text, index + 2);
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index = SkipBlockComment(text, index + 2);
                continue;
            }

            if (current == ';')
            {
                Emit(index + 1);
                statementStart = index + 1;
                index++;
                continue;
            }

            // T-SQL batch separator: a line that contains only GO (with an optional count).
            if ((current == 'G' || current == 'g')
                && IsLineStart(text, index)
                && MatchesBatchSeparator(text, index, out var separatorEnd))
            {
                Emit(index);
                statementStart = separatorEnd;
                index = separatorEnd;
                continue;
            }

            index++;
        }

        Emit(text.Length);
        return statements;
    }

    private static (int Start, int Length) TrimSpan(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);

        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (start, end - start);
    }

    /// <summary>False for a span that is only whitespace and comments: there is nothing to run.</summary>
    private static bool HasCode(string text, int start, int length)
    {
        var end = start + length;
        var index = start;

        while (index < end)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == '-' && index + 1 < end && text[index + 1] == '-')
            {
                index = SkipLineComment(text, index + 2);
                continue;
            }

            if (text[index] == '/' && index + 1 < end && text[index + 1] == '*')
            {
                index = SkipBlockComment(text, index + 2);
                continue;
            }

            return true;
        }

        return false;
    }

    private static int SkipQuoted(string text, int index, char quote)
    {
        index++;
        while (index < text.Length)
        {
            if (text[index] != quote)
            {
                index++;
                continue;
            }

            // SQL escapes a quote by doubling it.
            if (index + 1 < text.Length && text[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return text.Length;
    }

    private static int SkipBracket(string text, int index)
    {
        index++;
        while (index < text.Length)
        {
            if (text[index] != ']')
            {
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == ']')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return text.Length;
    }

    private static int SkipLineComment(string text, int index)
    {
        while (index < text.Length && text[index] is not ('\n' or '\r'))
        {
            index++;
        }

        return index;
    }

    private static int SkipBlockComment(string text, int index)
    {
        while (index + 1 < text.Length)
        {
            if (text[index] == '*' && text[index + 1] == '/')
            {
                return index + 2;
            }

            index++;
        }

        return text.Length;
    }

    private static bool IsLineStart(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = text[index - 1];
        return previous is '\n' or '\r';
    }

    private static bool MatchesBatchSeparator(string text, int index, out int end)
    {
        end = index;
        if (index + 2 > text.Length
            || !text.AsSpan(index, 2).Equals("GO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cursor = index + 2;
        if (cursor < text.Length && !char.IsWhiteSpace(text[cursor]))
        {
            return false;
        }

        // Optional repeat count, e.g. "GO 10".
        while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t'))
        {
            cursor++;
        }

        while (cursor < text.Length && char.IsDigit(text[cursor]))
        {
            cursor++;
        }

        while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t'))
        {
            cursor++;
        }

        if (cursor < text.Length && text[cursor] is not ('\n' or '\r'))
        {
            return false;
        }

        end = cursor;
        return true;
    }
}
