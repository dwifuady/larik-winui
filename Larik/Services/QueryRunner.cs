using Larik.Editor;

namespace Larik.Services;

/// <summary>
/// Turns an editor surface into the batches a run sends to the provider. The shell and a pop-out
/// window both run the tab's own query, so the "what does Ctrl+Enter mean" decision lives here
/// rather than in each window.
/// </summary>
public static class QueryRunner
{
    /// <summary>
    /// An explicit selection always wins; otherwise <paramref name="all"/> runs the whole document
    /// (F5) and the default runs the statement under the caret, selecting it so the run is visible.
    /// The provider's language owns statement and batch boundaries.
    /// </summary>
    public static IReadOnlyList<string> ResolveBatches(IEditorSurface editor, bool all)
    {
        var text = editor.Text;
        var selection = editor.Selection;

        if (!selection.IsEmpty)
        {
            return Split(editor, text.Substring(selection.Start, selection.Length));
        }

        if (all)
        {
            return Split(editor, text);
        }

        var statement = editor.EditorLanguage.StatementAt(text, editor.CaretOffset);
        if (!statement.IsEmpty)
        {
            editor.Select(new EditorSpan(statement.Start, statement.Length));
        }

        return [text.Substring(statement.Start, statement.Length)];
    }

    /// <summary>
    /// The provider's language owns batch boundaries (T-SQL splits on <c>GO</c>); the surface only
    /// turns the returned document spans into the strings the session sends.
    /// </summary>
    private static IReadOnlyList<string> Split(IEditorSurface editor, string text)
    {
        var spans = editor.EditorLanguage.SplitBatches(text);
        if (spans.Count == 0)
        {
            return [];
        }

        var batches = new List<string>(spans.Count);
        foreach (var span in spans)
        {
            batches.Add(text.Substring(span.Start, span.Length));
        }

        return batches;
    }
}
