namespace Larik.Data;

/// <summary>
/// Everything the shared SQL lexer and statement splitter can vary by dialect. A provider
/// assembly supplies an instance through <see cref="SqlLanguage"/>; it never reimplements the scan.
/// </summary>
public sealed class SqlLanguageOptions
{
    /// <summary>Identifier words coloured as keywords (the editor's token set, not completion).</summary>
    public required IReadOnlySet<string> TokenKeywords { get; init; }

    /// <summary>Treat <c>[x]</c> as an identifier with <c>]]</c> escaping (T-SQL).</summary>
    public bool BracketIdentifiers { get; init; }

    /// <summary>Treat <c>N'...'</c> as one Unicode string token (T-SQL).</summary>
    public bool UnicodeStringPrefix { get; init; }

    /// <summary>Characters that start an identifier: letters and <c>_</c> always do.</summary>
    public char[] IdentifierStartCharacters { get; init; } = ['@', '#'];

    /// <summary>Extra characters allowed inside an identifier.</summary>
    public char[] IdentifierPartCharacters { get; init; } = ['@', '#', '$'];

    /// <summary>Characters that start a variable token (T-SQL: <c>@</c>).</summary>
    public char[] VariablePrefixes { get; init; } = ['@'];

    /// <summary>Quote characters for string literals; doubled quotes escape.</summary>
    public char[] StringQuotes { get; init; } = ['\'', '"'];

    /// <summary>
    /// Quote characters that delimit identifiers (<c>"x"</c> in PostgreSQL). They are skipped so a
    /// semicolon inside a quoted name never splits a statement, but they keep the default colour.
    /// </summary>
    public char[] QuotedIdentifierQuotes { get; init; } = [];

    /// <summary>
    /// Treat <c>$tag$ ... $tag$</c> as one string token (PostgreSQL dollar quoting). The tag is
    /// optional, so <c>$$ ... $$</c> works too.
    /// </summary>
    public bool DollarQuotedStrings { get; init; }

    /// <summary>
    /// Treat <c>E'...'</c> as a string whose backslash escapes the next character (PostgreSQL).
    /// Ordinary strings still escape only by doubling the quote.
    /// </summary>
    public bool EscapeStringPrefix { get; init; }

    /// <summary>Block comments nest (<c>/* /* */ */</c>), as PostgreSQL allows.</summary>
    public bool NestedBlockComments { get; init; }

    public string[] LineCommentPrefixes { get; init; } = ["--"];

    public string BlockCommentOpen { get; init; } = "/*";

    public string BlockCommentClose { get; init; } = "*/";

    /// <summary>
    /// A batch separator that stands alone on a line, with an optional repeat count ("GO 10"), or
    /// <c>null</c> when the engine has none.
    /// </summary>
    public string? BatchSeparator { get; init; } = "GO";

    /// <summary>Split statements on <c>;</c> (always true for the engines this app ships).</summary>
    public bool SplitOnSemicolon { get; init; } = true;
}

/// <summary>
/// Reusable SQL lexer and statement splitter. Provider assemblies subclass this and add dialect
/// metadata (<see cref="ISqlDialect"/>) instead of copying the scan.
/// </summary>
public abstract class SqlLanguage : IEditorLanguage
{
    private readonly SqlLanguageOptions _options;
    private readonly HashSet<char> _identifierStarts;
    private readonly HashSet<char> _identifierParts;
    private readonly HashSet<char> _variablePrefixes;
    private readonly HashSet<char> _stringQuotes;
    private readonly HashSet<char> _quotedIdentifierQuotes;

    // Span lookup keeps keyword classification allocation-free on the typing path.
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _keywordLookup;

    protected SqlLanguage(SqlLanguageOptions options)
    {
        _options = options;
        _identifierStarts = new HashSet<char>(options.IdentifierStartCharacters);
        _identifierParts = new HashSet<char>(options.IdentifierPartCharacters);
        _variablePrefixes = new HashSet<char>(options.VariablePrefixes);
        _stringQuotes = new HashSet<char>(options.StringQuotes);
        _quotedIdentifierQuotes = new HashSet<char>(options.QuotedIdentifierQuotes);

        var keywords = new HashSet<string>(options.TokenKeywords, StringComparer.OrdinalIgnoreCase);
        _keywordLookup = keywords.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public abstract string Id { get; }

    public IReadOnlyList<QueryToken> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var tokens = new List<QueryToken>();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (StartsWithAny(text, index, _options.LineCommentPrefixes))
            {
                var start = index;
                index = SkipLineComment(text, index);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Comment));
                continue;
            }

            if (_options.BlockCommentOpen.Length > 0
                && text.AsSpan(index).StartsWith(_options.BlockCommentOpen, StringComparison.Ordinal))
            {
                var start = index;
                index = SkipBlockComment(text, index);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Comment));
                continue;
            }

            // N'...' is a Unicode string. The N is part of the literal, not an identifier.
            if (_options.UnicodeStringPrefix
                && (current == 'N' || current == 'n')
                && index + 1 < text.Length
                && text[index + 1] == '\'')
            {
                var start = index;
                index = SkipQuoted(text, index + 1, '\'');
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.String));
                continue;
            }

            // E'...' is a PostgreSQL escape string: backslash escapes the next character.
            if (_options.EscapeStringPrefix
                && (current == 'E' || current == 'e')
                && index + 1 < text.Length
                && text[index + 1] == '\'')
            {
                var start = index;
                index = SkipQuoted(text, index + 1, '\'', backslashEscapes: true);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.String));
                continue;
            }

            if (_stringQuotes.Contains(current))
            {
                var start = index;
                index = SkipQuoted(text, index, current);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.String));
                continue;
            }

            // "x" is an identifier in PostgreSQL, but a string in the historical T-SQL scanner.
            if (_quotedIdentifierQuotes.Contains(current))
            {
                index = SkipQuoted(text, index, current);
                continue;
            }

            // $tag$ ... $tag$ dollar quoting. Must come before identifiers so the body is one token.
            if (_options.DollarQuotedStrings
                && current == '$'
                && TrySkipDollarQuoted(text, index, out var dollarEnd))
            {
                tokens.Add(new QueryToken(index, dollarEnd - index, QueryTokenKind.String));
                index = dollarEnd;
                continue;
            }

            if (_options.BracketIdentifiers && current == '[')
            {
                // Bracketed identifiers keep the editor's default colour.
                index = SkipBracket(text, index);
                continue;
            }

            if (char.IsDigit(current))
            {
                var start = index;
                index = SkipNumber(text, index);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Number));
                continue;
            }

            if (IsIdentifierStart(current))
            {
                var start = index;
                var variable = _variablePrefixes.Contains(current);

                index++;
                while (index < text.Length && IsIdentifierPart(text[index]))
                {
                    index++;
                }

                if (variable)
                {
                    tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Variable));
                    continue;
                }

                if (_keywordLookup.Contains(text.AsSpan(start, index - start)))
                {
                    tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Keyword));
                }
                else if (NextNonWhitespace(text, index) == '(')
                {
                    tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Function));
                }

                continue;
            }

            index++;
        }

        return tokens;
    }

    public IReadOnlyList<StatementSpan> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var statements = new List<StatementSpan>();
        var statementStart = 0;
        var index = 0;

        void Emit(int end)
        {
            var (start, length) = EditorLanguageHelpers.Trim(text, statementStart, end);
            if (length > 0 && HasCode(text, start, length))
            {
                statements.Add(new StatementSpan(start, length));
            }
        }

        while (index < text.Length)
        {
            var current = text[index];

            if (_options.EscapeStringPrefix
                && (current == 'E' || current == 'e')
                && index + 1 < text.Length
                && text[index + 1] == '\'')
            {
                index = SkipQuoted(text, index + 1, '\'', backslashEscapes: true);
                continue;
            }

            if (_stringQuotes.Contains(current))
            {
                index = SkipQuoted(text, index, current);
                continue;
            }

            if (_quotedIdentifierQuotes.Contains(current))
            {
                index = SkipQuoted(text, index, current);
                continue;
            }

            if (_options.DollarQuotedStrings
                && current == '$'
                && TrySkipDollarQuoted(text, index, out var dollarEnd))
            {
                index = dollarEnd;
                continue;
            }

            if (_options.BracketIdentifiers && current == '[')
            {
                index = SkipBracket(text, index);
                continue;
            }

            if (_options.LineCommentPrefixes.Length > 0
                && StartsWithAny(text, index, _options.LineCommentPrefixes))
            {
                index = SkipLineComment(text, index);
                continue;
            }

            if (_options.BlockCommentOpen.Length > 0
                && text.AsSpan(index).StartsWith(_options.BlockCommentOpen, StringComparison.Ordinal))
            {
                index = SkipBlockComment(text, index);
                continue;
            }

            if (current == ';' && _options.SplitOnSemicolon)
            {
                Emit(index + 1);
                statementStart = index + 1;
                index++;
                continue;
            }

            if (_options.BatchSeparator is { Length: > 0 } separator
                && IsBatchSeparatorStart(text, index)
                && MatchesBatchSeparator(text, index, separator, out var separatorEnd))
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

    /// <summary>
    /// Splits text only on <c>GO</c> batch separators, keeping semicolons inside a batch. The shell
    /// sends each returned span to the server as its own command; SQL Server's driver does not
    /// understand <c>GO</c> (it is an SSMS/sqlcmd separator). A language with no batch separator
    /// sends the trimmed document as one batch.
    /// </summary>
    public IReadOnlyList<StatementSpan> SplitBatches(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (_options.BatchSeparator is not { Length: > 0 } separator)
        {
            // Comment-only text is not a batch: there is nothing to send to the server.
            var (start, length) = EditorLanguageHelpers.Trim(text, 0, text.Length);
            return length > 0 && HasCode(text, start, length)
                ? [new StatementSpan(start, length)]
                : [];
        }

        var batches = new List<StatementSpan>();
        var batchStart = 0;
        var index = 0;

        void Add(int end)
        {
            var (start, length) = EditorLanguageHelpers.Trim(text, batchStart, end);
            if (length > 0 && HasCode(text, start, length))
            {
                batches.Add(new StatementSpan(start, length));
            }
        }

        while (index < text.Length)
        {
            var current = text[index];

            if (_options.EscapeStringPrefix
                && (current == 'E' || current == 'e')
                && index + 1 < text.Length
                && text[index + 1] == '\'')
            {
                index = SkipQuoted(text, index + 1, '\'', backslashEscapes: true);
                continue;
            }

            if (_stringQuotes.Contains(current))
            {
                index = SkipQuoted(text, index, current);
                continue;
            }

            if (_quotedIdentifierQuotes.Contains(current))
            {
                index = SkipQuoted(text, index, current);
                continue;
            }

            if (_options.DollarQuotedStrings
                && current == '$'
                && TrySkipDollarQuoted(text, index, out var dollarEnd))
            {
                index = dollarEnd;
                continue;
            }

            if (_options.BracketIdentifiers && current == '[')
            {
                index = SkipBracket(text, index);
                continue;
            }

            if (StartsWithAny(text, index, _options.LineCommentPrefixes))
            {
                index = SkipLineComment(text, index);
                continue;
            }

            if (_options.BlockCommentOpen.Length > 0
                && text.AsSpan(index).StartsWith(_options.BlockCommentOpen, StringComparison.Ordinal))
            {
                index = SkipBlockComment(text, index);
                continue;
            }

            if ((current == 'G' || current == 'g')
                && IsBatchSeparatorStart(text, index)
                && MatchesBatchSeparator(text, index, separator, out var separatorEnd))
            {
                Add(index);
                batchStart = separatorEnd;
                index = separatorEnd;
                continue;
            }

            index++;
        }

        Add(text.Length);
        return batches;
    }

    /// <summary>
    /// The exact statement-at-caret behaviour the app shipped (SSMS-style Ctrl+Enter), including
    /// the comment-aware "nothing to run" check.
    /// </summary>
    public StatementSpan StatementAt(string? text, int caretOffset)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        var statements = Split(text);
        if (statements.Count == 0)
        {
            var (start, length) = EditorLanguageHelpers.Trim(text, 0, text.Length);
            return HasCode(text, start, length) ? new StatementSpan(start, length) : default;
        }

        var caret = Math.Clamp(caretOffset, 0, text.Length);

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

            if (index + 1 < statements.Count && caret < statements[index + 1].Start)
            {
                return statement;
            }
        }

        return statements[^1];
    }

    private bool IsIdentifierStart(char value) =>
        char.IsLetter(value) || value == '_' || _identifierStarts.Contains(value);

    private bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || _identifierParts.Contains(value);

    /// <summary>False for a span that is only whitespace and comments: there is nothing to run.</summary>
    private bool HasCode(string text, int start, int length)
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

            if (StartsWithAny(text, index, _options.LineCommentPrefixes))
            {
                index = SkipLineComment(text, index);
                continue;
            }

            if (_options.BlockCommentOpen.Length > 0
                && text.AsSpan(index).StartsWith(_options.BlockCommentOpen, StringComparison.Ordinal))
            {
                index = SkipBlockComment(text, index);
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool StartsWithAny(string text, int index, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (prefix.Length > 0
                && index + prefix.Length <= text.Length
                && text.AsSpan(index).StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int SkipQuoted(string text, int index, char quote, bool backslashEscapes = false)
    {
        index++;
        while (index < text.Length)
        {
            if (backslashEscapes && text[index] == '\\' && index + 1 < text.Length)
            {
                // PostgreSQL E'...' strings treat a backslash as an escape character.
                index += 2;
                continue;
            }

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

    /// <summary>
    /// Skips a PostgreSQL dollar-quoted string (<c>$$...$$</c>, <c>$tag$...$tag$</c>). Returns
    /// false when <paramref name="index"/> is not an opening delimiter, so <c>$1</c> parameters and
    /// <c>a$b</c> identifiers keep their ordinary meaning. Unterminated input consumes the rest,
    /// mirroring how every other unterminated literal behaves.
    /// </summary>
    private bool TrySkipDollarQuoted(string text, int index, out int end)
    {
        end = index;

        if (index >= text.Length || text[index] != '$')
        {
            return false;
        }

        // A dollar sign inside an identifier is not a delimiter: PostgreSQL allows a$b.
        if (index > 0 && IsIdentifierPart(text[index - 1]))
        {
            return false;
        }

        var cursor = index + 1;
        if (cursor >= text.Length)
        {
            return false;
        }

        // $$ immediately closes the tag.
        if (text[cursor] == '$')
        {
            var close = text.IndexOf("$$", cursor + 1, StringComparison.Ordinal);
            end = close < 0 ? text.Length : close + 2;
            return true;
        }

        if (!char.IsLetter(text[cursor]) && text[cursor] != '_')
        {
            return false;
        }

        var tagStart = cursor;
        while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_'))
        {
            cursor++;
        }

        if (cursor >= text.Length || text[cursor] != '$')
        {
            return false;
        }

        // Delimiter is $tag$: find the next identical one without allocating a substring.
        var tagLength = cursor - tagStart;
        var tag = text.AsSpan(tagStart, tagLength);
        var search = cursor + 1;

        while (search < text.Length)
        {
            var next = text.IndexOf('$', search);
            if (next < 0)
            {
                break;
            }

            if (next + tagLength + 1 < text.Length
                && text[next + tagLength + 1] == '$'
                && text.AsSpan(next + 1, tagLength).SequenceEqual(tag))
            {
                end = next + tagLength + 2;
                return true;
            }

            search = next + 1;
        }

        end = text.Length;
        return true;
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

    private int SkipBlockComment(string text, int index)
    {
        if (_options.BlockCommentClose.Length == 0)
        {
            return text.Length;
        }

        if (!_options.NestedBlockComments)
        {
            var close = text.IndexOf(_options.BlockCommentClose, index + _options.BlockCommentOpen.Length, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + _options.BlockCommentClose.Length;
        }

        // PostgreSQL allows block comments to nest, so count depth instead of stopping at the first */.
        var depth = 1;
        var cursor = index + _options.BlockCommentOpen.Length;
        while (cursor < text.Length)
        {
            if (text.AsSpan(cursor).StartsWith(_options.BlockCommentOpen, StringComparison.Ordinal))
            {
                depth++;
                cursor += _options.BlockCommentOpen.Length;
                continue;
            }

            if (text.AsSpan(cursor).StartsWith(_options.BlockCommentClose, StringComparison.Ordinal))
            {
                depth--;
                cursor += _options.BlockCommentClose.Length;
                if (depth == 0)
                {
                    return cursor;
                }

                continue;
            }

            cursor++;
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

    /// <summary>
    /// True when only whitespace precedes <paramref name="index"/> on its line, so an indented
    /// <c>GO</c> still separates batches (SSMS accepts indentation before the separator).
    /// </summary>
    private static bool IsBatchSeparatorStart(string text, int index)
    {
        var cursor = index - 1;
        while (cursor >= 0 && text[cursor] is not ('\n' or '\r'))
        {
            if (!char.IsWhiteSpace(text[cursor]))
            {
                return false;
            }

            cursor--;
        }

        return true;
    }

    private static bool MatchesBatchSeparator(string text, int index, string separator, out int end)
    {
        end = index;
        if (index + separator.Length > text.Length
            || !text.AsSpan(index, separator.Length).Equals(separator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cursor = index + separator.Length;
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

/// <summary>
/// The fallback language used when a provider exposes none (Demo and unknown providers). It is
/// configured to behave exactly like the app's historical lexer/splitter, including T-SQL-tolerant
/// details such as <c>[x]</c>, <c>N'...'</c>, <c>@</c> variables and <c>GO</c>.
/// </summary>
public sealed class GenericSqlLanguage : SqlLanguage
{
    /// <summary>The editor's historical token keyword set, shared with the T-SQL dialect.</summary>
    public static IReadOnlySet<string> TokenKeywords { get; } = new HashSet<string>(
        new[]
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
        },
        StringComparer.OrdinalIgnoreCase);

    public static GenericSqlLanguage Default { get; } = new();

    private GenericSqlLanguage()
        : base(new SqlLanguageOptions
        {
            TokenKeywords = TokenKeywords,
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

    public override string Id => "sql";
}
