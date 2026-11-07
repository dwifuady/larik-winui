namespace Larik.Services;

/// <summary>Lexical classes the editor colours. Only what a SQL writer recognises at a glance.</summary>
public enum SqlTokenKind
{
    Comment,
    String,
    Number,
    Keyword,
    Function,
    Variable,
}

/// <summary>A coloured range in document-text coordinates.</summary>
public readonly record struct SqlToken(int Start, int Length, SqlTokenKind Kind);

/// <summary>
/// A deliberately small SQL lexer for the editor's syntax highlighting. It never parses the
/// statement; it only needs to avoid colouring inside strings and comments and to recognise
/// keywords, functions, numbers and variables.
/// </summary>
public static class SqlSyntaxHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "AND", "ANY", "APPLY", "AS", "ASC", "BEGIN", "BETWEEN",
        "BREAK", "BY", "CASE", "CATCH", "CHECK", "COLUMN", "COMMIT", "CONSTRAINT",
        "CONTINUE", "CREATE", "CROSS", "DATABASE", "DECLARE", "DEFAULT", "DELETE", "DENY",
        "DESC", "DISTINCT", "DROP", "ELSE", "END", "EXEC", "EXECUTE", "EXISTS", "FETCH",
        "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION", "GO", "GRANT", "GROUP", "HAVING",
        "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT", "INTO", "IS", "JOIN", "KEY",
        "LEFT", "LIKE", "MERGE", "NEXT", "NOT", "NULL", "OFFSET", "ON", "ONLY", "OPTION",
        "OR", "ORDER", "OUTER", "OVER", "PARTITION", "PIVOT", "PRIMARY", "PRINT",
        "PROCEDURE", "REFERENCES", "RETURN", "REVOKE", "RIGHT", "ROLLBACK", "ROWCOUNT",
        "ROWS", "SCHEMA", "SELECT", "SET", "TABLE", "THEN", "TOP", "TRANSACTION",
        "TRIGGER", "TRUNCATE", "TRY", "UNION", "UNIQUE", "UNPIVOT", "UPDATE", "USE",
        "USING", "VALUES", "VIEW", "WAITFOR", "WHEN", "WHERE", "WHILE", "WITH",
    };

    public static IReadOnlyList<SqlToken> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var tokens = new List<SqlToken>();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                var start = index;
                index += 2;
                while (index < text.Length && text[index] is not ('\n' or '\r'))
                {
                    index++;
                }

                tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Comment));
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var start = index;
                index = SkipBlockComment(text, index + 2);
                tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Comment));
                continue;
            }

            // N'...' is a Unicode string. The N is part of the literal, not an identifier.
            if ((current == 'N' || current == 'n')
                && index + 1 < text.Length
                && text[index + 1] == '\'')
            {
                var start = index;
                index = SkipQuoted(text, index + 1, '\'');
                tokens.Add(new SqlToken(start, index - start, SqlTokenKind.String));
                continue;
            }

            if (current is '\'' or '"')
            {
                var start = index;
                index = SkipQuoted(text, index, current);
                tokens.Add(new SqlToken(start, index - start, SqlTokenKind.String));
                continue;
            }

            if (current == '[')
            {
                // Bracketed identifiers keep the editor's default colour.
                index = SkipBracket(text, index);
                continue;
            }

            if (char.IsDigit(current))
            {
                var start = index;
                index = SkipNumber(text, index);
                tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Number));
                continue;
            }

            if (current == '@' || current == '#' || IsIdentifierStart(current))
            {
                var start = index;
                var variable = current == '@';

                index++;
                while (index < text.Length && IsIdentifierPart(text[index]))
                {
                    index++;
                }

                if (variable)
                {
                    tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Variable));
                    continue;
                }

                var word = text.AsSpan(start, index - start);
                if (Keywords.Contains(word.ToString()))
                {
                    tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Keyword));
                }
                else if (NextNonWhitespace(text, index) == '(')
                {
                    tokens.Add(new SqlToken(start, index - start, SqlTokenKind.Function));
                }

                continue;
            }

            index++;
        }

        return tokens;
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

    private static int SkipNumber(string text, int index)
    {
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.'))
        {
            index++;
        }

        if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            index++;
            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
            {
                index++;
            }

            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }
        }

        return index;
    }

    private static char NextNonWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length ? text[index] : '\0';
    }

    private static bool IsIdentifierStart(char value) =>
        char.IsLetter(value) || value == '_';

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '@' or '#' or '$';
}
