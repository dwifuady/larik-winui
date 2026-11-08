using Larik.Data;
using Larik.Editor;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Larik.Controls;

/// <summary>
/// The RichEditBox implementation of <see cref="IEditorSurface"/>: plain-text SQL editing,
/// lightweight syntax highlighting, and a suggestion popup anchored to the real caret
/// (<c>ITextSelection.GetRect</c>), where Tab or Enter accepts the highlighted snippet.
/// </summary>
public sealed partial class SqlEditorView : UserControl, IEditorSurface
{
    private const double PopupWidth = 392;
    private const double PopupMaxHeight = 268;

    private bool _suppressEvents;
    private bool _skipCompletionUpdateOnce;
    private bool _applyingHighlight;
    private bool _restoringHistory;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _highlightTimer;
    private IReadOnlyList<CompletionItem>? _completions;
    private int _selectedIndex = -1;

    // Character formatting (syntax highlighting) is undoable in RichEdit, which would make
    // Ctrl+Z reverse colours instead of text. The surface keeps its own text snapshots and
    // leaves the control's undo stack disabled.
    private const int MaxUndoEntries = 200;
    private static readonly TimeSpan UndoCoalesce = TimeSpan.FromMilliseconds(700);
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private string _lastKnownText = string.Empty;
    private int _editAnchor = -1;
    private DateTime _lastEditAt = DateTime.MinValue;

    private readonly record struct UndoEntry(string Text, int Caret);

    public SqlEditorView()
    {
        InitializeComponent();

        // Own undo: formatting changes must never occupy the undo stack.
        Editor.Document.UndoLimit = 0;

        // Theme resources change while the app runs; token colours must follow.
        ActualThemeChanged += (_, _) => ApplyHighlighting();
    }

    public event EventHandler? TextChanged;

    public event EventHandler? CaretChanged;

    public event EventHandler<EditorCommandKind>? CommandRequested;

    public IEditorCompletionSource? CompletionSource { get; set; }

    private IEditorLanguage _language = GenericSqlLanguage.Default;

    /// <summary>
    /// The active editor language. Changing it re-highlights with the new tokenizer; the shell sets
    /// it from the active space's provider.
    /// </summary>
    public IEditorLanguage EditorLanguage
    {
        get => _language;
        set
        {
            var language = value ?? GenericSqlLanguage.Default;
            if (ReferenceEquals(language, _language))
            {
                return;
            }

            _language = language;
            ScheduleHighlighting();
        }
    }

    /// <summary>Programmatic text access. Setting it does not raise <see cref="TextChanged"/>.</summary>
    public string Text
    {
        get => GetText();
        set => SetText(value ?? string.Empty);
    }

    public EditorCaret Caret => ComputeCaret(GetText(), Editor.Document.Selection.StartPosition);

    public int CaretOffset =>
        Math.Clamp(Editor.Document.Selection.StartPosition, 0, GetText().Length);

    public EditorSpan Selection
    {
        get
        {
            var selection = Editor.Document.Selection;
            var length = GetText().Length;
            var start = Math.Clamp(Math.Min(selection.StartPosition, selection.EndPosition), 0, length);
            var end = Math.Clamp(Math.Max(selection.StartPosition, selection.EndPosition), start, length);
            return new EditorSpan(start, end - start);
        }
    }

    public void Select(EditorSpan span)
    {
        var length = GetText().Length;
        var start = Math.Clamp(span.Start, 0, length);
        var end = Math.Clamp(span.End, start, length);
        Editor.Document.Selection.SetRange(start, end);
    }

    public void FocusEditor() => Editor.Focus(FocusState.Programmatic);

    // ---------------------------------------------------------------- text plumbing

    private string GetText()
    {
        Editor.Document.GetText(TextGetOptions.None, out var text);
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private void SetText(string text)
    {
        // A different document (tab switch) invalidates both the query's offsets and the bar
        // itself; the bar belongs to the editor, not to the tab you left.
        if (FindBar.Visibility == Visibility.Visible)
        {
            CloseFind(focusEditor: false);
        }

        _suppressEvents = true;
        try
        {
            Editor.Document.SetText(TextSetOptions.None, text);
            Editor.Document.Selection.SetRange(0, 0);
        }
        finally
        {
            _suppressEvents = false;
        }

        HideCompletions();
        ResetHistory(GetText());
        ApplyHighlighting();
        RaiseCaretChanged();
    }

    private static EditorCaret ComputeCaret(string text, int position)
    {
        var offset = Math.Clamp(position, 0, text.Length);
        var line = 1;
        var lineStart = 0;

        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\n' || (text[index] == '\r' && (index + 1 >= offset || text[index + 1] != '\n')))
            {
                line++;
                lineStart = index + 1;
            }
        }

        return new EditorCaret(line, offset - lineStart + 1);
    }

    private void RaiseCaretChanged() => CaretChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseTextChanged() => TextChanged?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- syntax highlighting

    /// <summary>
    /// Re-colouring on every keystroke would fight the caret and the undo stack; wait for a
    /// pause in typing. The tokenizer is a single pass, so a whole-document repaint stays cheap.
    /// </summary>
    private void ScheduleHighlighting()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        if (_highlightTimer is null)
        {
            _highlightTimer = queue.CreateTimer();
            _highlightTimer.Interval = TimeSpan.FromMilliseconds(140);
            _highlightTimer.IsRepeating = false;
            _highlightTimer.Tick += (_, _) => ApplyHighlighting();
        }

        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void ApplyHighlighting()
    {
        if (_applyingHighlight || !IsLoaded || Editor.XamlRoot is null)
        {
            return;
        }

        _applyingHighlight = true;
        var wasSuppressed = _suppressEvents;
        _suppressEvents = true;

        try
        {
            var text = GetText();
            if (text.Length == 0)
            {
                return;
            }

            var selection = Editor.Document.Selection;
            var selectionStart = selection.StartPosition;
            var selectionEnd = selection.EndPosition;

            // Reset the whole story first so a deleted keyword does not leave its colour behind.
            SetForeground(0, text.Length, TokenColor(null));

            foreach (var token in EditorLanguage.Tokenize(text))
            {
                var start = Math.Clamp(token.Start, 0, text.Length);
                var end = Math.Clamp(token.Start + token.Length, start, text.Length);
                if (end > start)
                {
                    SetForeground(start, end, TokenColor(token.Kind));
                }
            }

            selection.SetRange(selectionStart, selectionEnd);
        }
        catch (Exception)
        {
            // Highlighting is best-effort presentation. A transient RichEdit formatting failure
            // must never take the whole studio down; the next keystroke retries it.
        }
        finally
        {
            _suppressEvents = wasSuppressed;
            _applyingHighlight = false;
        }
    }

    private void SetForeground(int start, int end, Color color)
    {
        // Follow the documented pattern: mutate the format object, then assign it back.
        // Setting the ABI property directly has crashed RichEdit in this control.
        var range = Editor.Document.GetRange(start, end);
        var format = range.CharacterFormat;
        format.ForegroundColor = color;
        range.CharacterFormat = format;
    }

    private static Color TokenColor(QueryTokenKind? kind) =>
        ColorOf(kind switch
        {
            QueryTokenKind.Comment => "StudioSyntaxCommentBrush",
            QueryTokenKind.String => "StudioSyntaxStringBrush",
            QueryTokenKind.Number => "StudioSyntaxNumberBrush",
            QueryTokenKind.Keyword => "StudioSyntaxKeywordBrush",
            QueryTokenKind.Function => "StudioSyntaxFunctionBrush",
            QueryTokenKind.Variable => "StudioSyntaxVariableBrush",
            _ => "StudioTextPrimaryBrush",
        });

    private static Color ColorOf(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush
            ? brush.Color
            : Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF7);

    // ---------------------------------------------------------------- undo / redo

    private void ResetHistory(string text)
    {
        _undo.Clear();
        _redo.Clear();
        _lastKnownText = text;
        _lastEditAt = DateTime.MinValue;
        _editAnchor = -1;
    }

    /// <summary>Captures the pre-edit state. Typing within the coalesce window is one entry.</summary>
    private void BeginEdit()
    {
        var now = DateTime.UtcNow;
        if (_undo.Count == 0 || now - _lastEditAt > UndoCoalesce)
        {
            _undo.Add(new UndoEntry(
                _lastKnownText,
                _editAnchor >= 0 ? _editAnchor : CurrentCaretOffset()));

            if (_undo.Count > MaxUndoEntries)
            {
                _undo.RemoveAt(0);
            }
        }

        _redo.Clear();
        _lastEditAt = now;
        _editAnchor = -1;
    }

    private void EndEdit() => _lastKnownText = GetText();

    private void UndoEdit()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new UndoEntry(GetText(), CurrentCaretOffset()));
        Restore(entry);
    }

    private void RedoEdit()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new UndoEntry(GetText(), CurrentCaretOffset()));
        Restore(entry);
    }

    private void Restore(UndoEntry entry)
    {
        _restoringHistory = true;
        _suppressEvents = true;
        try
        {
            Editor.Document.SetText(TextSetOptions.None, entry.Text);
            var length = GetText().Length;
            var caret = Math.Clamp(entry.Caret, 0, length);
            Editor.Document.Selection.SetRange(caret, caret);
        }
        finally
        {
            _suppressEvents = false;
            _restoringHistory = false;
        }

        _lastKnownText = GetText();
        _lastEditAt = DateTime.MinValue;
        _editAnchor = -1;

        HideCompletions();
        ApplyHighlighting();
        RaiseTextChanged();
        RaiseCaretChanged();

        if (FindBar.Visibility == Visibility.Visible)
        {
            RefreshAfterEdit();
        }
    }

    private int CurrentCaretOffset()
    {
        var selection = Editor.Document.Selection;
        return Math.Clamp(Math.Min(selection.StartPosition, selection.EndPosition), 0, GetText().Length);
    }

    // ---------------------------------------------------------------- editor events

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        // Character formatting can raise a delayed TextChanged of its own. If the text is
        // unchanged, this is not a user edit: do not create undo history or reopen the
        // completion popup at a caret the user did not move.
        var text = GetText();
        if (text == _lastKnownText)
        {
            return;
        }

        if (!_restoringHistory)
        {
            BeginEdit();
        }

        RaiseTextChanged();
        RaiseCaretChanged();
        ScheduleHighlighting();
        EndEdit();

        if (FindBar.Visibility == Visibility.Visible)
        {
            RefreshAfterEdit();
        }

        // Inserting a completion can raise a delayed TextChanged after the popup closes. Do not
        // immediately reopen it with the next clause's suggestions; wait for the next real key.
        if (_skipCompletionUpdateOnce)
        {
            _skipCompletionUpdateOnce = false;
            return;
        }

        UpdateCompletions();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var selection = Editor.Document.Selection;
        if (selection.StartPosition != selection.EndPosition)
        {
            HideCompletions();
        }

        RaiseCaretChanged();
    }

    private void Editor_LostFocus(object sender, RoutedEventArgs e) => HideCompletions();

    // ---------------------------------------------------------------- suggestions

    private void UpdateCompletions()
    {
        if (CompletionSource is null)
        {
            return;
        }

        var text = GetText();
        var caret = Editor.Document.Selection.StartPosition;

        // The source decides what a word means; the surface only knows geometry. An empty word
        // still completes when the clause calls for a table or column list (e.g. "FROM ").
        var items = CompletionSource.GetCompletions(new CompletionQuery(text, caret, EditorLanguage));
        if (items.Count == 0)
        {
            HideCompletions();
            return;
        }

        _completions = items;
        CompletionList.ItemsSource = items;
        SelectCompletion(0);
        CompletionPopup.IsOpen = true;
        PositionCompletions();
    }

    private void PositionCompletions()
    {
        // GetRect reports the caret inside the text layout area, but the popup is placed from
        // this control's origin. Add the editor's position and padding so the popup starts at
        // the caret and one line below the text.
        Editor.Document.Selection.GetRect(PointOptions.ClientCoordinates, out var caretRect, out _);
        var editorOrigin = Editor.TransformToVisual(this).TransformPoint(new Point(0, 0));
        var padding = Editor.Padding;
        var origin = new Point(
            editorOrigin.X + padding.Left + caretRect.X,
            editorOrigin.Y + padding.Top + caretRect.Y + caretRect.Height + 6);

        CompletionPopup.Child.Measure(new Size(PopupWidth, double.PositiveInfinity));
        var height = Math.Min(CompletionPopup.Child.DesiredSize.Height, PopupMaxHeight);

        var x = Math.Clamp(origin.X, 8, Math.Max(8, ActualWidth - PopupWidth - 8));
        var y = origin.Y;
        if (y + height > ActualHeight - 8)
        {
            // No room below the caret: flip above the current line.
            y = Math.Max(8, origin.Y - height - caretRect.Height - padding.Top - 10);
        }

        CompletionPopup.HorizontalOffset = x;
        CompletionPopup.VerticalOffset = y;
    }

    private void SelectCompletion(int index)
    {
        if (_completions is null || _completions.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _completions.Count - 1);

        for (var i = 0; i < _completions.Count; i++)
        {
            _completions[i].IsSelected = i == index;
        }

        _selectedIndex = index;
        CompletionList.ScrollIntoView(_completions[index]);
    }

    private void HideCompletions()
    {
        CompletionPopup.IsOpen = false;
        CompletionList.ItemsSource = null;
        _completions = null;
        _selectedIndex = -1;
    }

    private void AcceptCompletion()
    {
        if (_completions is null || _selectedIndex < 0)
        {
            return;
        }

        _skipCompletionUpdateOnce = true;
        var item = _completions[_selectedIndex];
        var caret = Editor.Document.Selection.StartPosition;
        var (start, _, _) = CompletionEngine.ExtractContext(GetText(), caret);

        BeginEdit();
        _suppressEvents = true;
        try
        {
            var selection = Editor.Document.Selection;
            selection.SetRange(start, caret);
            selection.Text = item.InsertText;

            if (item.Selection is { } placeholder)
            {
                selection.SetRange(start + placeholder.Start, start + placeholder.Start + placeholder.Length);
            }
            else
            {
                var caretAt = item.CaretOffset >= 0
                    ? start + item.CaretOffset
                    : start + item.InsertText.Length;
                selection.SetRange(caretAt, caretAt);
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        EndEdit();
        HideCompletions();
        RaiseTextChanged();
        RaiseCaretChanged();
        FocusEditor();
    }

    private void CompletionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CompletionItem item || _completions is null)
        {
            return;
        }

        var index = _completions.ToList().IndexOf(item);
        if (index >= 0)
        {
            SelectCompletion(index);
            AcceptCompletion();
        }
    }

    // ---------------------------------------------------------------- find / replace

    // Match offsets are cached per query; any edit invalidates the list instead of rescanning
    // while the user types in the editor.
    private readonly List<int> _matches = [];
    private int _matchIndex = -1;
    private int _findOrigin;
    private bool _matchesDirty = true;
    private bool _suppressFindUpdates;
    private string? _findFeedback;

    private string FindQuery => FindBox.Text ?? string.Empty;

    private void OpenFind(bool showReplace)
    {
        HideCompletions();

        if (FindBar.Visibility != Visibility.Visible)
        {
            // Seed from the selection like every other editor: select a word, Ctrl+F, find it.
            var selection = Selection;
            if (!selection.IsEmpty && selection.Length <= 128)
            {
                var seed = GetText().Substring(selection.Start, selection.Length)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();

                _suppressFindUpdates = true;
                FindBox.Text = seed;
                _suppressFindUpdates = false;
            }

            _findOrigin = selection.IsEmpty ? CaretOffset : selection.Start;
        }

        FindBar.Visibility = Visibility.Visible;
        SetReplaceVisible(showReplace || ReplaceRow.Visibility == Visibility.Visible);

        _findFeedback = null;
        _matchesDirty = true;
        SelectFirstMatch();
        FocusFindBox();
    }

    private void FocusFindBox()
    {
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
    }

    private void SetReplaceVisible(bool visible)
    {
        ReplaceRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReplaceChevron.Glyph = visible ? "\uE70D" : "\uE76C";
    }

    private void CloseFind(bool focusEditor = true)
    {
        FindBar.Visibility = Visibility.Collapsed;
        _matches.Clear();
        _matchIndex = -1;
        _matchesDirty = true;
        _findFeedback = null;

        if (focusEditor)
        {
            FocusEditor();
        }
    }

    private void EnsureMatches()
    {
        if (!_matchesDirty)
        {
            return;
        }

        _matches.Clear();
        var query = FindQuery;
        var text = GetText();

        if (query.Length > 0 && text.Length >= query.Length)
        {
            var search = 0;
            var last = text.Length - query.Length;
            while (search <= last)
            {
                var hit = text.IndexOf(query, search, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    break;
                }

                _matches.Add(hit);
                search = hit + query.Length;
            }
        }

        _matchesDirty = false;
    }

    private void UpdateFindCount()
    {
        var text = _findFeedback
            ?? (FindQuery.Length == 0
                ? string.Empty
                : _matches.Count == 0
                    ? "No results"
                    : _matchIndex is >= 0 && _matchIndex < _matches.Count
                        ? $"{_matchIndex + 1} of {_matches.Count}"
                        : $"{_matches.Count} matches");

        FindCountText.Text = text;
        // The count is the accessible feedback; keep the announced name equal to what is shown.
        AutomationProperties.SetName(FindCountText, text.Length > 0 ? text : "Find matches");
    }

    private void SelectFirstMatch()
    {
        EnsureMatches();
        if (_matches.Count == 0)
        {
            _matchIndex = -1;
            UpdateFindCount();
            return;
        }

        var index = _matches.FindIndex(start => start >= _findOrigin);
        ShowMatch(index >= 0 ? index : 0);
    }

    private void ShowMatch(int index)
    {
        _matchIndex = Math.Clamp(index, 0, _matches.Count - 1);
        _findOrigin = _matches[_matchIndex];

        // The real selection is the highlight: it follows the theme/high-contrast selection brush
        // and cannot fight the syntax colours, which live in the same character format.
        Select(new EditorSpan(_findOrigin, FindQuery.Length));
        try
        {
            Editor.Document.Selection.ScrollIntoView(PointOptions.None);
        }
        catch (Exception)
        {
            // Scrolling is cosmetic; RichEdit can reject it while the layout is settling.
        }

        UpdateFindCount();
    }

    private void FindNext()
    {
        _findFeedback = null;
        EnsureMatches();

        if (_matches.Count == 0)
        {
            _matchIndex = -1;
            UpdateFindCount();
            return;
        }

        ShowMatch(_matchIndex < 0 ? 0 : (_matchIndex + 1) % _matches.Count);
    }

    private void FindPrevious()
    {
        _findFeedback = null;
        EnsureMatches();

        if (_matches.Count == 0)
        {
            _matchIndex = -1;
            UpdateFindCount();
            return;
        }

        ShowMatch(_matchIndex < 0 ? _matches.Count - 1 : (_matchIndex - 1 + _matches.Count) % _matches.Count);
    }

    /// <summary>Recomputes what changed without moving the caret the user is typing with.</summary>
    private void RefreshAfterEdit()
    {
        _matchesDirty = true;
        _matchIndex = -1;
        EnsureMatches();
        UpdateFindCount();
    }

    /// <summary>
    /// Applies a find/replace edit as one undoable step and publishes it like a user edit. The
    /// control's own TextChanged stays suppressed so the completion popup does not open while
    /// focus is in the find bar.
    /// </summary>
    private void ApplyFindEdit(int caretAnchor, Action apply)
    {
        _editAnchor = Math.Max(0, caretAnchor);
        BeginEdit();
        _suppressEvents = true;
        try
        {
            apply();
        }
        finally
        {
            _suppressEvents = false;
        }

        EndEdit();
        HideCompletions();
        RaiseTextChanged();
        RaiseCaretChanged();
        ScheduleHighlighting();
    }

    /// <summary>RichEdit paragraph marks are CR, so a pasted multi-line replacement must use CR too.</summary>
    private static string NormalizeInsertion(string text) =>
        text.Replace("\r\n", "\n").Replace('\n', '\r');

    private void ReplaceCurrent()
    {
        _findFeedback = null;
        EnsureMatches();

        if (_matches.Count == 0)
        {
            UpdateFindCount();
            return;
        }

        if (_matchIndex < 0 || _matchIndex >= _matches.Count)
        {
            // Nothing is highlighted yet: show the next match instead of guessing which one the
            // user means, then a second press replaces it.
            FindNext();
            return;
        }

        var start = _matches[_matchIndex];
        var length = FindQuery.Length;
        var replacement = NormalizeInsertion(ReplaceBox.Text ?? string.Empty);

        ApplyFindEdit(start, () => Editor.Document.GetRange(start, start + length).Text = replacement);

        _findOrigin = start + replacement.Length;
        _matchesDirty = true;
        SelectFirstMatch();
    }

    private void ReplaceAll()
    {
        _findFeedback = null;
        EnsureMatches();

        var query = FindQuery;
        if (query.Length == 0 || _matches.Count == 0)
        {
            UpdateFindCount();
            return;
        }

        var starts = _matches.ToArray();
        var replacement = NormalizeInsertion(ReplaceBox.Text ?? string.Empty);

        // Back to front: replacing later text cannot move the offsets of earlier matches.
        ApplyFindEdit(starts[0], () =>
        {
            for (var i = starts.Length - 1; i >= 0; i--)
            {
                Editor.Document.GetRange(starts[i], starts[i] + query.Length).Text = replacement;
            }
        });

        _findFeedback = $"Replaced {starts.Length}";
        _matches.Clear();
        _matchesDirty = true;
        _matchIndex = -1;
        _findOrigin = 0;
        UpdateFindCount();
    }

    // ---------------------------------------------------------------- find bar events

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFindUpdates)
        {
            return;
        }

        _findFeedback = null;
        _matchesDirty = true;
        SelectFirstMatch();
    }

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when IsKeyDown(VirtualKey.Shift):
                FindPrevious();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
            case VirtualKey.F3:
                FindNext();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                CloseFind();
                e.Handled = true;
                break;
            case VirtualKey.F when IsKeyDown(VirtualKey.Control):
                // Pressing Ctrl+F again should reselect the query, not close or duplicate it.
                FindBox.SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.H when IsKeyDown(VirtualKey.Control):
                SetReplaceVisible(true);
                ReplaceBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                break;
        }
    }

    private void ReplaceBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                ReplaceCurrent();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    private void ToggleReplace_Click(object sender, RoutedEventArgs e)
    {
        var show = ReplaceRow.Visibility != Visibility.Visible;
        SetReplaceVisible(show);
        if (show)
        {
            ReplaceBox.Focus(FocusState.Programmatic);
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindPrevious();

    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceCurrent();

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAll();

    // ---------------------------------------------------------------- keyboard

    private void Editor_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Any new key means the user is typing again; a pending post-accept guard no longer applies.
        _skipCompletionUpdateOnce = false;

        // Remember where the edit starts, so undo can put the caret back at that point.
        var selection = Editor.Document.Selection;
        _editAnchor = Math.Min(selection.StartPosition, selection.EndPosition);

        // RichEditBox swallows Ctrl+Enter and owns Ctrl+B/Ctrl+T, so shell commands are raised here.
        // F5 runs the whole query; Ctrl+Enter runs only the statement under the caret.
        if (e.Key == VirtualKey.F5)
        {
            HideCompletions();
            CommandRequested?.Invoke(this, EditorCommandKind.RunAll);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter && IsKeyDown(VirtualKey.Control))
        {
            HideCompletions();
            CommandRequested?.Invoke(this, EditorCommandKind.RunStatement);
            e.Handled = true;
            return;
        }

        if (IsKeyDown(VirtualKey.Control) && !IsKeyDown(VirtualKey.Shift) && !IsKeyDown(VirtualKey.Menu))
        {
            switch (e.Key)
            {
                case VirtualKey.B:
                    CommandRequested?.Invoke(this, EditorCommandKind.ToggleSidebar);
                    e.Handled = true;
                    return;
                case VirtualKey.T:
                    CommandRequested?.Invoke(this, EditorCommandKind.NewTab);
                    e.Handled = true;
                    return;
                case VirtualKey.F:
                    OpenFind(showReplace: false);
                    e.Handled = true;
                    return;
                case VirtualKey.H:
                    OpenFind(showReplace: true);
                    e.Handled = true;
                    return;

                // VK_OEM_5 is the backslash key. Running to a new result tab must not clear
                // the results already shown for this query tab.
                case (VirtualKey)220:
                    HideCompletions();
                    CommandRequested?.Invoke(this, EditorCommandKind.RunNewResult);
                    e.Handled = true;
                    return;
            }
        }

        if (IsKeyDown(VirtualKey.Control) && !IsKeyDown(VirtualKey.Menu))
        {
            if (e.Key == VirtualKey.Z && !IsKeyDown(VirtualKey.Shift))
            {
                UndoEdit();
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Y || (e.Key == VirtualKey.Z && IsKeyDown(VirtualKey.Shift)))
            {
                RedoEdit();
                e.Handled = true;
                return;
            }
        }

        if (CompletionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case VirtualKey.Tab:
                case VirtualKey.Enter:
                    AcceptCompletion();
                    e.Handled = true;
                    return;
                case VirtualKey.Down:
                    SelectCompletion(_selectedIndex + 1);
                    e.Handled = true;
                    return;
                case VirtualKey.Up:
                    SelectCompletion(_selectedIndex - 1);
                    e.Handled = true;
                    return;
                case VirtualKey.Escape:
                    HideCompletions();
                    e.Handled = true;
                    return;
            }

            return;
        }

        // F3 repeats the last find; with the bar closed it opens it on the first match instead of
        // skipping one. Shift+F3 walks backwards.
        if (e.Key == VirtualKey.F3 && FindBar.Visibility == Visibility.Visible)
        {
            if (IsKeyDown(VirtualKey.Shift))
            {
                FindPrevious();
            }
            else
            {
                FindNext();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.F3)
        {
            OpenFind(showReplace: false);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && FindBar.Visibility == Visibility.Visible)
        {
            CloseFind();
            e.Handled = true;
            return;
        }

        // Tab keeps focus in the editor and indents; completion accept owns Tab while open.
        if (e.Key == VirtualKey.Tab)
        {
            BeginEdit();
            _suppressEvents = true;
            try
            {
                Editor.Document.Selection.Text = "    ";
            }
            finally
            {
                _suppressEvents = false;
            }

            EndEdit();
            RaiseTextChanged();
            RaiseCaretChanged();
            e.Handled = true;
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
