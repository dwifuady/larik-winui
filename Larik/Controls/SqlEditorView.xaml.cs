using Larik.Editor;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Larik.Controls;

/// <summary>
/// The RichEditBox implementation of <see cref="IEditorSurface"/>: plain-text SQL editing plus a
/// suggestion popup anchored to the real caret (<c>ITextSelection.GetRect</c>), where Tab or Enter
/// accepts the highlighted snippet.
/// </summary>
public sealed partial class SqlEditorView : UserControl, IEditorSurface
{
    private const double PopupWidth = 392;
    private const double PopupMaxHeight = 268;

    private bool _suppressEvents;
    private bool _skipCompletionUpdateOnce;
    private IReadOnlyList<CompletionItem>? _completions;
    private int _selectedIndex = -1;

    public SqlEditorView()
    {
        InitializeComponent();
    }

    public event EventHandler? TextChanged;

    public event EventHandler? CaretChanged;

    public event EventHandler<EditorCommandKind>? CommandRequested;

    public IEditorCompletionSource? CompletionSource { get; set; }

    /// <summary>Programmatic text access. Setting it does not raise <see cref="TextChanged"/>.</summary>
    public string Text
    {
        get => GetText();
        set => SetText(value ?? string.Empty);
    }

    public EditorCaret Caret => ComputeCaret(GetText(), Editor.Document.Selection.StartPosition);

    public void FocusEditor() => Editor.Focus(FocusState.Programmatic);

    // ---------------------------------------------------------------- text plumbing

    private string GetText()
    {
        Editor.Document.GetText(TextGetOptions.None, out var text);
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private void SetText(string text)
    {
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
        RaiseCaretChanged();
    }

    private static EditorCaret ComputeCaret(string text, int position)
    {
        var offset = Math.Clamp(position, 0, text.Length);
        var before = text[..offset];
        var line = before.Count(c => c == '\n') + 1;
        var column = before.Length - (before.LastIndexOf('\n') + 1) + 1;
        return new EditorCaret(line, column);
    }

    private void RaiseCaretChanged() => CaretChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseTextChanged() => TextChanged?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- editor events

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        RaiseTextChanged();
        RaiseCaretChanged();

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
        var items = CompletionSource.GetCompletions(new CompletionQuery(text, caret));
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

    // ---------------------------------------------------------------- keyboard

    private void Editor_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Any new key means the user is typing again; a pending post-accept guard no longer applies.
        _skipCompletionUpdateOnce = false;

        // RichEditBox swallows Ctrl+Enter and owns Ctrl+B/Ctrl+T, so shell commands are raised here.
        if (e.Key is VirtualKey.Enter or VirtualKey.F5 && (e.Key == VirtualKey.F5 || IsKeyDown(VirtualKey.Control)))
        {
            HideCompletions();
            CommandRequested?.Invoke(this, EditorCommandKind.RunQuery);
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

                // VK_OEM_5 is the backslash key. Running to a new result tab must not clear
                // the results already shown for this query tab.
                case (VirtualKey)220:
                    HideCompletions();
                    CommandRequested?.Invoke(this, EditorCommandKind.RunNewResult);
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

        // Tab keeps focus in the editor and indents; completion accept owns Tab while open.
        if (e.Key == VirtualKey.Tab)
        {
            _suppressEvents = true;
            try
            {
                Editor.Document.Selection.Text = "    ";
            }
            finally
            {
                _suppressEvents = false;
            }

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
