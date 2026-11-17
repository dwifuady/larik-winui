using Windows.Foundation;

namespace Larik.Editor;

/// <summary>
/// One marker the editor paints for a note range. Markers are grouped by range: a statement with
/// three notes is one marker with <see cref="Count"/> three, not three overlapping badges.
/// </summary>
public sealed record EditorNoteMarker(int Start, int Length, int Count);

/// <summary>
/// The optional seam between notes and an editor surface. <see cref="IEditorSurface"/> deliberately
/// stays about text, caret and commands; a surface that can paint range markers implements this in
/// addition, and an implementation that cannot is still a valid editor.
/// <para>
/// The shell supplies markers (it owns notes) and reacts to <see cref="NoteMarkerInvoked"/>; the
/// geometry method lets the shell place the note editor beside the anchored text without reaching
/// into the RichEdit document.
/// </para>
/// </summary>
public interface IEditorNoteSurface
{
    /// <summary>Raised when the user clicks a marker. The payload is the range that was clicked.</summary>
    event EventHandler<EditorNoteMarker>? NoteMarkerInvoked;

    /// <summary>Replaces the painted markers. Ranges are in <see cref="IEditorSurface.Text"/> coordinates.</summary>
    void SetNoteMarkers(IReadOnlyList<EditorNoteMarker> markers);

    /// <summary>Draws the accent bar for the note being edited; null clears it.</summary>
    void SetActiveNoteAnchor(EditorSpan? span);

    /// <summary>Selects a range and scrolls it into view, without taking focus from the caller.</summary>
    void RevealSpan(EditorSpan span);

    /// <summary>
    /// Bounds of a range in this control's coordinate space, or an empty rectangle when the range is
    /// not laid out. Used to anchor the note editor popover.
    /// </summary>
    Rect GetSpanBounds(EditorSpan span);
}
