using System.Buffers;
using System.Collections.Frozen;

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
    [Flags]
    private enum AsciiKind : byte
    {
        None = 0,
        Whitespace = 1 << 0,
        Digit = 1 << 1,
        IdentStart = 1 << 2,
        IdentPart = 1 << 3,
        VariablePrefix = 1 << 4,
        StringQuote = 1 << 5,
        QuotedIdentQuote = 1 << 6,
    }

    private readonly SqlLanguageOptions _options;
    private readonly HashSet<char> _identifierStarts;
    private readonly HashSet<char> _identifierParts;
    private readonly HashSet<char> _variablePrefixes;
    private readonly HashSet<char> _stringQuotes;
    private readonly HashSet<char> _quotedIdentifierQuotes;

    // 128-entry ASCII classification table keeps per-character checks branch-light and avoids
    // HashSet<char>.Contains lookups on the hot typing/splitting paths.
    private readonly byte[] _ascii = new byte[128];

    // Cached fast-path prefix fields so the lexer avoids iterating LineCommentPrefixes on every char.
    private readonly string[] _lineCommentPrefixes;
    private readonly char _lineCommentFirstChar;
    private readonly bool _singleLineCommentPrefix;
    private readonly string _blockCommentOpen;
    private readonly string _blockCommentClose;
    private readonly char _blockCommentFirstChar;
    private readonly string? _batchSeparator;
    private readonly char _batchSepFirstUpper;
    private readonly char _batchSepFirstLower;
    private readonly bool _bracketIdentifiers;
    private readonly bool _unicodeStringPrefix;
    private readonly bool _escapeStringPrefix;
    private readonly bool _dollarQuotedStrings;
    private readonly bool _nestedBlockComments;
    private readonly bool _splitOnSemicolon;

    // SIMD-accelerated trigger sets for Split / SplitBatches so ordinary SQL characters are skipped
    // in bulk instead of inspecting every character through a chain of branches.
    private readonly SearchValues<char> _splitTriggers;
    private readonly SearchValues<char> _batchSplitTriggers;

    // FrozenSet + span alternate lookup keeps keyword classification fast and allocation-free.
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _keywordLookup;

    protected SqlLanguage(SqlLanguageOptions options)
    {
        _options = options;
        _identifierStarts = new HashSet<char>(options.IdentifierStartCharacters);
        _identifierParts = new HashSet<char>(options.IdentifierPartCharacters);
        _variablePrefixes = new HashSet<char>(options.VariablePrefixes);
        _stringQuotes = new HashSet<char>(options.StringQuotes);
        _quotedIdentifierQuotes = new HashSet<char>(options.QuotedIdentifierQuotes);

        _lineCommentPrefixes = options.LineCommentPrefixes;
        _singleLineCommentPrefix = _lineCommentPrefixes.Length == 1 && _lineCommentPrefixes[0].Length > 0;
        _lineCommentFirstChar = _singleLineCommentPrefix ? _lineCommentPrefixes[0][0] : '\0';
        _blockCommentOpen = options.BlockCommentOpen;
        _blockCommentClose = options.BlockCommentClose;
        _blockCommentFirstChar = _blockCommentOpen.Length > 0 ? _blockCommentOpen[0] : '\0';
        _batchSeparator = options.BatchSeparator is { Length: > 0 } sep ? sep : null;
        _batchSepFirstUpper = _batchSeparator is not null ? char.ToUpperInvariant(_batchSeparator[0]) : '\0';
        _batchSepFirstLower = _batchSeparator is not null ? char.ToLowerInvariant(_batchSeparator[0]) : '\0';
        _bracketIdentifiers = options.BracketIdentifiers;
        _unicodeStringPrefix = options.UnicodeStringPrefix;
        _escapeStringPrefix = options.EscapeStringPrefix;
        _dollarQuotedStrings = options.DollarQuotedStrings;
        _nestedBlockComments = options.NestedBlockComments;
        _splitOnSemicolon = options.SplitOnSemicolon;

        for (var i = 0; i < 128; i++)
        {
            var c = (char)i;
            var flags = AsciiKind.None;
            if (char.IsWhiteSpace(c)) flags |= AsciiKind.Whitespace;
            if (c is >= '0' and <= '9') flags |= AsciiKind.Digit | AsciiKind.IdentPart;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_') flags |= AsciiKind.IdentStart | AsciiKind.IdentPart;
            if (_identifierStarts.Contains(c)) flags |= AsciiKind.IdentStart;
            if (_identifierParts.Contains(c)) flags |= AsciiKind.IdentPart;
            if (_variablePrefixes.Contains(c)) flags |= AsciiKind.VariablePrefix;
            if (_stringQuotes.Contains(c)) flags |= AsciiKind.StringQuote;
            if (_quotedIdentifierQuotes.Contains(c)) flags |= AsciiKind.QuotedIdentQuote;
            _ascii[i] = (byte)flags;
        }

        var triggerChars = new HashSet<char>();
        foreach (var c in options.StringQuotes) triggerChars.Add(c);
        foreach (var c in options.QuotedIdentifierQuotes) triggerChars.Add(c);
        if (_escapeStringPrefix) triggerChars.Add('\'');
        if (_dollarQuotedStrings) triggerChars.Add('$');
        if (_bracketIdentifiers) triggerChars.Add('[');
        foreach (var prefix in _lineCommentPrefixes)
        {
            if (prefix.Length > 0) triggerChars.Add(prefix[0]);
        }

        if (_blockCommentOpen.Length > 0) triggerChars.Add(_blockCommentOpen[0]);
        if (_batchSeparator is not null)
        {
            triggerChars.Add(_batchSepFirstUpper);
            triggerChars.Add(_batchSepFirstLower);
        }

        _batchSplitTriggers = SearchValues.Create([.. triggerChars]);

        if (_splitOnSemicolon) triggerChars.Add(';');
        _splitTriggers = SearchValues.Create([.. triggerChars]);

        var keywords = options.TokenKeywords.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _keywordLookup = keywords.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public abstract string Id { get; }

    public IReadOnlyList<QueryToken> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var span = text.AsSpan();
        var ascii = _ascii;
        // Pre-size to avoid repeated List<QueryToken> growth/memmove on large SQL scripts.
        var tokens = new List<QueryToken>(Math.Clamp(span.Length / 10, 16, 65_536));
        var index = 0;
        var length = span.Length;

        while (index < length)
        {
            var current = span[index];
            var flags = current < 128 ? (AsciiKind)ascii[current] : AsciiKind.None;

            if ((flags & AsciiKind.Whitespace) != 0 || (current >= 128 && char.IsWhiteSpace(current)))
            {
                index++;
                continue;
            }

            if (StartsWithLineComment(span, index, current))
            {
                var start = index;
                index = SkipLineComment(span, index);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Comment));
                continue;
            }

            if (current == _blockCommentFirstChar
                && _blockCommentOpen.Length > 0
                && span.Slice(index).StartsWith(_blockCommentOpen, StringComparison.Ordinal))
            {
                var start = index;
                index = SkipBlockComment(span, index);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Comment));
                continue;
            }

            // N'...' is a Unicode string. The N is part of the literal, not an identifier.
            if (_unicodeStringPrefix
                && (current == 'N' || current == 'n')
                && index + 1 < length
                && span[index + 1] == '\'')
            {
                var start = index;
                index = SkipQuoted(span, index + 1, '\'');
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.String));
                continue;
            }

            // E'...' is a PostgreSQL escape string: backslash escapes the next character.
            if (_escapeStringPrefix
                && (current == 'E' || current == 'e')
                && index + 1 < length
                && span[index + 1] == '\'')
            {
                var start = index;
                index = SkipQuoted(span, index + 1, '\'', backslashEscapes: true);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.String));
                continue;
            }

            if ((flags & AsciiKind.StringQuote) != 0 || (current >= 128 && _stringQuotes.Contains(current)))
            {
                var start = index;
                index = SkipQuoted(span, index, current);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.String));
                continue;
            }

            // "x" is an identifier in PostgreSQL, but a string in the historical T-SQL scanner.
            if ((flags & AsciiKind.QuotedIdentQuote) != 0 || (current >= 128 && _quotedIdentifierQuotes.Contains(current)))
            {
                index = SkipQuoted(span, index, current);
                continue;
            }

            // $tag$ ... $tag$ dollar quoting. Must come before identifiers so the body is one token.
            if (_dollarQuotedStrings
                && current == '$'
                && TrySkipDollarQuoted(span, index, out var dollarEnd))
            {
                tokens.Add(new QueryToken(index, dollarEnd - index, QueryTokenKind.String));
                index = dollarEnd;
                continue;
            }

            if (_bracketIdentifiers && current == '[')
            {
                // Bracketed identifiers keep the editor's default colour.
                index = SkipBracket(span, index);
                continue;
            }

            if ((flags & AsciiKind.Digit) != 0 || (current >= 128 && char.IsDigit(current)))
            {
                var start = index;
                index = SkipNumber(span, index);
                tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Number));
                continue;
            }

            if ((flags & AsciiKind.IdentStart) != 0 || (current >= 128 && IsIdentifierStartNonAscii(current)))
            {
                var start = index;
                var variable = (flags & AsciiKind.VariablePrefix) != 0
                    || (current >= 128 && _variablePrefixes.Contains(current));

                index++;
                while (index < length)
                {
                    var ch = span[index];
                    if (ch < 128)
                    {
                        if (((AsciiKind)ascii[ch] & AsciiKind.IdentPart) == 0)
                        {
                            break;
                        }
                    }
                    else if (!IsIdentifierPartNonAscii(ch))
                    {
                        break;
                    }

                    index++;
                }

                if (variable)
                {
                    tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Variable));
                    continue;
                }

                if (_keywordLookup.Contains(span.Slice(start, index - start)))
                {
                    tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Keyword));
                }
                else if (NextNonWhitespace(span, index) == '(')
                {
                    tokens.Add(new QueryToken(start, index - start, QueryTokenKind.Function));
                }

                continue;
            }

            index++;
        }

        return tokens;
    }

    public virtual IReadOnlyList<StatementSpan> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var span = text.AsSpan();
        var length = span.Length;
        var ascii = _ascii;
        var triggers = _splitTriggers;
        var statements = new List<StatementSpan>();
        var statementStart = 0;
        var scanStart = 0;
        var index = 0;

        while (index < length)
        {
            var nextOffset = span.Slice(index).IndexOfAny(triggers);
            if (nextOffset < 0)
            {
                break;
            }

            index += nextOffset;
            var current = span[index];

            if (current == ';' && _splitOnSemicolon)
            {
                EmitStatement(statements, text, statementStart, index + 1);
                statementStart = index + 1;
                index++;
                scanStart = index;
                continue;
            }

            if (_escapeStringPrefix
                && current == '\''
                && index > scanStart
                && (span[index - 1] == 'E' || span[index - 1] == 'e'))
            {
                index = SkipQuoted(span, index, '\'', backslashEscapes: true);
                scanStart = index;
                continue;
            }

            var flags = current < 128 ? (AsciiKind)ascii[current] : AsciiKind.None;
            if ((flags & AsciiKind.StringQuote) != 0 || (current >= 128 && _stringQuotes.Contains(current)))
            {
                index = SkipQuoted(span, index, current);
                scanStart = index;
                continue;
            }

            if ((flags & AsciiKind.QuotedIdentQuote) != 0 || (current >= 128 && _quotedIdentifierQuotes.Contains(current)))
            {
                index = SkipQuoted(span, index, current);
                scanStart = index;
                continue;
            }

            if (_dollarQuotedStrings
                && current == '$'
                && TrySkipDollarQuoted(span, index, out var dollarEnd))
            {
                index = dollarEnd;
                scanStart = index;
                continue;
            }

            if (_bracketIdentifiers && current == '[')
            {
                index = SkipBracket(span, index);
                scanStart = index;
                continue;
            }

            if (StartsWithLineComment(span, index, current))
            {
                index = SkipLineComment(span, index);
                scanStart = index;
                continue;
            }

            if (current == _blockCommentFirstChar
                && _blockCommentOpen.Length > 0
                && span.Slice(index).StartsWith(_blockCommentOpen, StringComparison.Ordinal))
            {
                index = SkipBlockComment(span, index);
                scanStart = index;
                continue;
            }

            if (_batchSeparator is not null
                && (current == _batchSepFirstUpper || current == _batchSepFirstLower)
                && IsBatchSeparatorStart(span, index)
                && MatchesBatchSeparator(span, index, _batchSeparator, out var separatorEnd))
            {
                EmitStatement(statements, text, statementStart, index);
                statementStart = separatorEnd;
                index = separatorEnd;
                scanStart = index;
                continue;
            }

            index++;
        }

        EmitStatement(statements, text, statementStart, length);
        return statements;
    }

    /// <summary>
    /// Splits text only on <c>GO</c> batch separators, keeping semicolons inside a batch. The shell
    /// sends each returned span to the server as its own command; SQL Server's driver does not
    /// understand <c>GO</c> (it is an SSMS/sqlcmd separator). A language with no batch separator
    /// sends the trimmed document as one batch.
    /// </summary>
    public virtual IReadOnlyList<StatementSpan> SplitBatches(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (_batchSeparator is not { } separator)
        {
            // Comment-only text is not a batch: there is nothing to send to the server.
            var (start, trimLength) = EditorLanguageHelpers.Trim(text, 0, text.Length);
            return trimLength > 0 && HasCode(text.AsSpan(), start, trimLength)
                ? [new StatementSpan(start, trimLength)]
                : [];
        }

        var span = text.AsSpan();
        var length = span.Length;
        var ascii = _ascii;
        var triggers = _batchSplitTriggers;
        var batches = new List<StatementSpan>();
        var batchStart = 0;
        var scanStart = 0;
        var index = 0;

        while (index < length)
        {
            var nextOffset = span.Slice(index).IndexOfAny(triggers);
            if (nextOffset < 0)
            {
                break;
            }

            index += nextOffset;
            var current = span[index];

            if (_escapeStringPrefix
                && current == '\''
                && index > scanStart
                && (span[index - 1] == 'E' || span[index - 1] == 'e'))
            {
                index = SkipQuoted(span, index, '\'', backslashEscapes: true);
                scanStart = index;
                continue;
            }

            var flags = current < 128 ? (AsciiKind)ascii[current] : AsciiKind.None;
            if ((flags & AsciiKind.StringQuote) != 0 || (current >= 128 && _stringQuotes.Contains(current)))
            {
                index = SkipQuoted(span, index, current);
                scanStart = index;
                continue;
            }

            if ((flags & AsciiKind.QuotedIdentQuote) != 0 || (current >= 128 && _quotedIdentifierQuotes.Contains(current)))
            {
                index = SkipQuoted(span, index, current);
                scanStart = index;
                continue;
            }

            if (_dollarQuotedStrings
                && current == '$'
                && TrySkipDollarQuoted(span, index, out var dollarEnd))
            {
                index = dollarEnd;
                scanStart = index;
                continue;
            }

            if (_bracketIdentifiers && current == '[')
            {
                index = SkipBracket(span, index);
                scanStart = index;
                continue;
            }

            if (StartsWithLineComment(span, index, current))
            {
                index = SkipLineComment(span, index);
                scanStart = index;
                continue;
            }

            if (current == _blockCommentFirstChar
                && _blockCommentOpen.Length > 0
                && span.Slice(index).StartsWith(_blockCommentOpen, StringComparison.Ordinal))
            {
                index = SkipBlockComment(span, index);
                scanStart = index;
                continue;
            }

            if ((current == _batchSepFirstUpper || current == _batchSepFirstLower)
                && IsBatchSeparatorStart(span, index)
                && MatchesBatchSeparator(span, index, separator, out var separatorEnd))
            {
                EmitStatement(batches, text, batchStart, index);
                batchStart = separatorEnd;
                index = separatorEnd;
                scanStart = index;
                continue;
            }

            index++;
        }

        EmitStatement(batches, text, batchStart, length);
        return batches;
    }

    private void EmitStatement(List<StatementSpan> target, string text, int statementStart, int end)
    {
        var (start, length) = EditorLanguageHelpers.Trim(text, statementStart, end);
        if (length > 0 && HasCode(text.AsSpan(), start, length))
        {
            target.Add(new StatementSpan(start, length));
        }
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
            return HasCode(text.AsSpan(), start, length) ? new StatementSpan(start, length) : default;
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

    private bool IsIdentifierStartNonAscii(char value) =>
        char.IsLetter(value) || _identifierStarts.Contains(value);

    private bool IsIdentifierPartNonAscii(char value) =>
        char.IsLetterOrDigit(value) || _identifierParts.Contains(value);

    private bool IsIdentifierPart(char value) =>
        value < 128
            ? ((AsciiKind)_ascii[value] & AsciiKind.IdentPart) != 0
            : IsIdentifierPartNonAscii(value);

    private bool StartsWithLineComment(ReadOnlySpan<char> text, int index, char current)
    {
        if (_singleLineCommentPrefix)
        {
            if (current != _lineCommentFirstChar)
            {
                return false;
            }

            var prefix = _lineCommentPrefixes[0];
            return prefix.Length == 2
                ? index + 1 < text.Length && text[index + 1] == prefix[1]
                : text.Slice(index).StartsWith(prefix, StringComparison.Ordinal);
        }

        return StartsWithAny(text, index, _lineCommentPrefixes);
    }

    /// <summary>False for a span that is only whitespace and comments: there is nothing to run.</summary>
    private bool HasCode(ReadOnlySpan<char> text, int start, int length)
    {
        var end = start + length;
        var index = start;

        while (index < end)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (StartsWithLineComment(text, index, current))
            {
                index = SkipLineComment(text, index);
                continue;
            }

            if (current == _blockCommentFirstChar
                && _blockCommentOpen.Length > 0
                && text.Slice(index).StartsWith(_blockCommentOpen, StringComparison.Ordinal))
            {
                index = SkipBlockComment(text, index);
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool StartsWithAny(ReadOnlySpan<char> text, int index, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (prefix.Length > 0
                && index + prefix.Length <= text.Length
                && text.Slice(index).StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int SkipQuoted(ReadOnlySpan<char> text, int index, char quote, bool backslashEscapes = false)
    {
        index++;
        if (!backslashEscapes)
        {
            while (index < text.Length)
            {
                var offset = text.Slice(index).IndexOf(quote);
                if (offset < 0)
                {
                    return text.Length;
                }

                index += offset;
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
                // PostgreSQL E'...' strings treat a backslash as an escape character.
                if (index + 1 < text.Length)
                {
                    index += 2;
                    continue;
                }

                return text.Length;
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
    private bool TrySkipDollarQuoted(ReadOnlySpan<char> text, int index, out int end)
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
            var closeOffset = text.Slice(cursor + 1).IndexOf("$$", StringComparison.Ordinal);
            end = closeOffset < 0 ? text.Length : cursor + 1 + closeOffset + 2;
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
        var tag = text.Slice(tagStart, tagLength);
        var search = cursor + 1;

        while (search < text.Length)
        {
            var nextOffset = text.Slice(search).IndexOf('$');
            if (nextOffset < 0)
            {
                break;
            }

            var next = search + nextOffset;
            if (next + tagLength + 1 < text.Length
                && text[next + tagLength + 1] == '$'
                && text.Slice(next + 1, tagLength).SequenceEqual(tag))
            {
                end = next + tagLength + 2;
                return true;
            }

            search = next + 1;
        }

        end = text.Length;
        return true;
    }

    private static int SkipBracket(ReadOnlySpan<char> text, int index)
    {
        index++;
        while (index < text.Length)
        {
            var offset = text.Slice(index).IndexOf(']');
            if (offset < 0)
            {
                return text.Length;
            }

            index += offset;
            if (index + 1 < text.Length && text[index + 1] == ']')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return text.Length;
    }

    private static int SkipLineComment(ReadOnlySpan<char> text, int index)
    {
        var offset = text.Slice(index).IndexOfAny('\n', '\r');
        return offset < 0 ? text.Length : index + offset;
    }

    private int SkipBlockComment(ReadOnlySpan<char> text, int index)
    {
        if (_blockCommentClose.Length == 0)
        {
            return text.Length;
        }

        if (!_nestedBlockComments)
        {
            var searchStart = index + _blockCommentOpen.Length;
            var closeOffset = text.Slice(searchStart).IndexOf(_blockCommentClose, StringComparison.Ordinal);
            return closeOffset < 0 ? text.Length : searchStart + closeOffset + _blockCommentClose.Length;
        }

        // PostgreSQL allows block comments to nest, so count depth instead of stopping at the first */.
        var depth = 1;
        var cursor = index + _blockCommentOpen.Length;
        var openFirst = _blockCommentOpen[0];
        var closeFirst = _blockCommentClose[0];
        while (cursor < text.Length)
        {
            var offset = text.Slice(cursor).IndexOfAny(openFirst, closeFirst);
            if (offset < 0)
            {
                return text.Length;
            }

            cursor += offset;
            if (text.Slice(cursor).StartsWith(_blockCommentOpen, StringComparison.Ordinal))
            {
                depth++;
                cursor += _blockCommentOpen.Length;
                continue;
            }

            if (text.Slice(cursor).StartsWith(_blockCommentClose, StringComparison.Ordinal))
            {
                depth--;
                cursor += _blockCommentClose.Length;
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

    private static int SkipNumber(ReadOnlySpan<char> text, int index)
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

    private static char NextNonWhitespace(ReadOnlySpan<char> text, int index)
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
    private static bool IsBatchSeparatorStart(ReadOnlySpan<char> text, int index)
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

    private static bool MatchesBatchSeparator(ReadOnlySpan<char> text, int index, string separator, out int end)
    {
        end = index;
        if (index + separator.Length > text.Length
            || !text.Slice(index, separator.Length).Equals(separator, StringComparison.OrdinalIgnoreCase))
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
