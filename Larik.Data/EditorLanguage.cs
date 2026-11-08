namespace Larik.Data;

/// <summary>
/// Lexical classes an editor can colour. Deliberately small: the editor does not parse, it only
/// needs to avoid painting inside strings/comments and to make keywords and calls recognisable.
/// </summary>
public enum QueryTokenKind
{
    Comment,
    String,
    Number,
    Keyword,
    Function,
    Variable,
}

/// <summary>A classified range in document-text coordinates.</summary>
public readonly record struct QueryToken(int Start, int Length, QueryTokenKind Kind);

/// <summary>
/// One runnable statement's span in document-text coordinates.
/// <para>
/// <see cref="Language"/> tags mixed-script documents (for example a MongoDB script can contain
/// both <c>db.users.find()</c> and a SQL <c>SELECT</c>); <c>null</c> means "whatever language
/// produced the span". It is a string rather than an enum so a provider can add tags without a
/// contract change.
/// </para>
/// </summary>
public readonly record struct StatementSpan(int Start, int Length, string? Language = null)
{
    public int End => Start + Length;

    public bool IsEmpty => Length <= 0;
}

/// <summary>
/// The editor language a provider owns: highlighting tokens and statement boundaries.
/// <para>
/// A SQL engine implements <see cref="ISqlDialect"/>; a non-SQL engine (MongoDB later) implements
/// this interface only. The app talks to this interface and never branches on a provider id, so
/// adding an engine means adding a provider assembly, not editing the editor.
/// </para>
/// </summary>
public interface IEditorLanguage
{
    /// <summary>Stable id, e.g. "tsql", "postgres", "mongodb". Not the same as provider id.</summary>
    string Id { get; }

    /// <summary>
    /// Classifies the document for syntax highlighting. Called on a typing debounce, so it must be
    /// a single synchronous pass with no I/O, no reflection and no expensive allocation.
    /// </summary>
    IReadOnlyList<QueryToken> Tokenize(string? text);

    /// <summary>
    /// Spans of every non-empty statement with runnable content, in document order. Spans include
    /// their trailing terminator (for example the <c>;</c>) exactly as the editor always has.
    /// Comments-only and whitespace-only spans are not statements.
    /// </summary>
    IReadOnlyList<StatementSpan> Split(string? text);

    /// <summary>
    /// Splits text into the units sent to the server as one command. An engine with a batch
    /// separator (T-SQL <c>GO</c>) splits there and keeps semicolons inside a batch; an engine
    /// without one sends the trimmed document as a single batch. Spans are trimmed and empty or
    /// comment-only input yields no batches.
    /// </summary>
    IReadOnlyList<StatementSpan> SplitBatches(string? text) =>
        EditorLanguageHelpers.SingleBatch(text);

    /// <summary>
    /// The statement a caret belongs to. The default keeps the SSMS behaviour this app has always
    /// had: a caret in the whitespace after a statement belongs to that statement, so Ctrl+Enter on
    /// a blank line repeats what the user just looked at.
    /// </summary>
    StatementSpan StatementAt(string? text, int caretOffset) =>
        EditorLanguageHelpers.StatementAt(this, text, caretOffset);
}

/// <summary>
/// A SQL dialect: an editor language plus the metadata the completion engine needs. MongoDB (and
/// any other non-SQL engine) must not implement this; it implements <see cref="IEditorLanguage"/>.
/// </summary>
public interface ISqlDialect : IEditorLanguage
{
    /// <summary>Completion keywords. Multi-word phrases are allowed ("INNER JOIN").</summary>
    IReadOnlyList<string> Keywords { get; }

    /// <summary>Built-in functions offered in expression positions.</summary>
    IReadOnlyList<string> Functions { get; }

    /// <summary>Words after which a table name is expected (FROM, JOIN, INTO, ...).</summary>
    IReadOnlyList<string> TableContextKeywords { get; }

    /// <summary>Quotes an identifier the way this engine requires ("[x]" or "\"x\"").</summary>
    string QuoteIdentifier(string name);
}

/// <summary>Shared entry points for resolving and querying an <see cref="IEditorLanguage"/>.</summary>
public static class EditorLanguageHelpers
{
    /// <summary>
    /// The language a provider exposes, or the generic SQL fallback when it exposes none. A null
    /// provider (no catalogue match) also gets the fallback, never another exception path.
    /// </summary>
    public static IEditorLanguage Resolve(IDatabaseProvider? provider) =>
        provider?.EditorLanguage ?? GenericSqlLanguage.Default;

    /// <summary>
    /// Default <see cref="IEditorLanguage.StatementAt"/> implementation. Languages that know their
    /// comment syntax (see <see cref="SqlLanguage"/>) override it with a comment-aware version.
    /// </summary>
    public static StatementSpan StatementAt(IEditorLanguage language, string? text, int caretOffset)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        var statements = language.Split(text);
        if (statements.Count == 0)
        {
            var (start, length) = Trim(text, 0, text.Length);
            return length > 0 && HasNonWhitespace(text, start, length)
                ? new StatementSpan(start, length)
                : default;
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

    /// <summary>One trimmed batch spanning the whole document, when the language has no separator.</summary>
    internal static IReadOnlyList<StatementSpan> SingleBatch(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var (start, length) = Trim(text, 0, text.Length);
        return length > 0 && HasNonWhitespace(text, start, length)
            ? [new StatementSpan(start, length)]
            : [];
    }

    internal static (int Start, int Length) Trim(string text, int start, int end)
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

    internal static bool HasNonWhitespace(string text, int start, int length)
    {
        var end = Math.Min(start + length, text.Length);
        for (var index = Math.Max(start, 0); index < end; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return true;
            }
        }

        return false;
    }
}
