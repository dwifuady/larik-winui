using Larik.Data;
using Larik.Models;

namespace Larik.Editor;

/// <summary>Shell commands an editor surface can raise, so keyboard behaviour is implementation-agnostic.</summary>
public enum EditorCommandKind
{
    /// <summary>Run the statement under the caret (selections win).</summary>
    RunStatement,

    /// <summary>Run the whole document, or the selection when there is one.</summary>
    RunAll,

    /// <summary>Run the statement under the caret into a new result tab.</summary>
    RunNewResult,

    NewTab,
    ToggleSidebar,

    /// <summary>Open the schema peek (Ctrl+K).</summary>
    BrowseSchema,
}

/// <summary>One-based caret position in the document.</summary>
public readonly record struct EditorCaret(int Line, int Column);

/// <summary>A span in document-text coordinates, used for selections and statement runs.</summary>
public readonly record struct EditorSpan(int Start, int Length)
{
    public int End => Start + Length;

    public bool IsEmpty => Length <= 0;
}

/// <summary>
/// The text window the editor is asking about. Implementations pass the document and the caret
/// offset rather than a pre-parsed word, so every surface shares one context extraction. The
/// language lets a provider-specific surface answer with its own keywords.
/// </summary>
public sealed record CompletionQuery(
    string DocumentText,
    int CaretOffset,
    IEditorLanguage? Language = null);

/// <summary>What the shell knows that the editor needs in order to suggest anything.</summary>
public interface IEditorCompletionSource
{
    IReadOnlyList<CompletionItem> GetCompletions(CompletionQuery query);
}

/// <summary>
/// The editable SQL surface. The shell talks to this interface only, so the current
/// <see cref="Controls.SqlEditorView"/> (RichEditBox + custom popup) can be replaced by a
/// WebView2/Monaco island without touching the rail, tabs, spaces, toolbar or results grid.
/// <para>
/// An implementation owns its own visuals, key handling and suggestion UI. It may be asynchronous
/// underneath (WebView2): <see cref="Text"/> and <see cref="Caret"/> may then be served from the last
/// value reported by the host runtime, and assignments applied asynchronously.
/// </para>
/// </summary>
public interface IEditorSurface
{
    /// <summary>
    /// Full document text. Setting it is programmatic (tab switch, Format) and must not raise
    /// <see cref="TextChanged"/>; the caller already knows the new value.
    /// </summary>
    string Text { get; set; }

    EditorCaret Caret { get; }

    /// <summary>Zero-based caret offset in <see cref="Text"/> coordinates.</summary>
    int CaretOffset { get; }

    /// <summary>The current selection span; equal start/end when collapsed.</summary>
    EditorSpan Selection { get; }

    /// <summary>Selects a span without moving focus (used to show the statement being run).</summary>
    void Select(EditorSpan span);

    /// <summary>
    /// Replaces the current selection with <paramref name="text"/> and leaves the caret after it.
    /// The schema browser inserts through this so the shell never assembles editor text itself.
    /// </summary>
    void InsertText(string text);

    /// <summary>Supplies snippets/keywords/schema. The shell swaps this when the space or schema changes.</summary>
    IEditorCompletionSource? CompletionSource { get; set; }

    /// <summary>
    /// The active editor language: tokens for highlighting, spans for statement runs and (for SQL)
    /// dialect metadata for completion. The shell resolves this from the active connection; the
    /// surface falls back to generic SQL when it has nothing better.
    /// </summary>
    IEditorLanguage EditorLanguage { get; set; }

    /// <summary>Raised for user edits only.</summary>
    event EventHandler? TextChanged;

    event EventHandler? CaretChanged;

    /// <summary>Raised when the user asks for a shell action from inside the editor.</summary>
    event EventHandler<EditorCommandKind>? CommandRequested;

    void FocusEditor();
}
