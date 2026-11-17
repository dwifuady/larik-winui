using Larik.Services;
using Larik.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Larik.Controls;

/// <summary>
/// The anchored note surface: kind, quote, body. It edits the note object directly and has no Save
/// button - the shell debounces the write, so closing the popover is never a way to lose a note.
/// </summary>
public sealed partial class NoteEditorView : UserControl
{
    private NoteViewModel? _note;
    private bool _loading;

    public NoteEditorView() => InitializeComponent();

    public event EventHandler? CloseRequested;

    public event EventHandler? DeleteRequested;

    public NoteViewModel? Note
    {
        get => _note;
        set
        {
            if (ReferenceEquals(_note, value))
            {
                return;
            }

            _note = value;
            LoadNote();
        }
    }

    public Visibility QuoteVisibility => _note is { HasQuote: true } ? Visibility.Visible : Visibility.Collapsed;

    public void FocusBody()
    {
        BodyBox.Focus(FocusState.Programmatic);
        BodyBox.SelectionStart = BodyBox.Text.Length;
    }

    /// <summary>Refreshes quote and anchor after the shell resolved or re-anchored the same note.</summary>
    public void Reload() => LoadNote();

    private void LoadNote()
    {
        _loading = true;
        try
        {
            QuoteText.Text = _note?.QuotePreview ?? string.Empty;
            AnchorText.Text = _note?.AnchorText ?? string.Empty;
            BodyBox.Text = _note?.Body ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }

        // x:Bind is one-time for this small surface; the quote visibility changes per note.
        Bindings.Update();
        UpdateKindStyles();
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _note is null)
        {
            return;
        }

        _note.Body = BodyBox.Text;
    }

    private void Kind_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null || sender is not Button { Tag: string tag })
        {
            return;
        }

        _note.Kind = tag switch
        {
            "Todo" => NoteKind.Todo,
            "Decision" => NoteKind.Decision,
            "Observation" => NoteKind.Observation,
            _ => NoteKind.Note,
        };

        UpdateKindStyles();
    }

    private void UpdateKindStyles()
    {
        var kind = _note?.Kind ?? NoteKind.Note;
        StyleFor(NoteKindButton, kind == NoteKind.Note);
        StyleFor(TodoKindButton, kind == NoteKind.Todo);
        StyleFor(DecisionKindButton, kind == NoteKind.Decision);
        StyleFor(ObservationKindButton, kind == NoteKind.Observation);

        void StyleFor(Button button, bool selected) =>
            button.Style = (Style)Resources[selected ? "NoteKindPillSelectedStyle" : "NoteKindPillStyle"];
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this, EventArgs.Empty);

    private void BodyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
