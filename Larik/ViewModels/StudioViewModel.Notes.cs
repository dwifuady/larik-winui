using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Larik.Data;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Xaml;

namespace Larik.ViewModels;

/// <summary>
/// The notes half of the shell: loading notes with their tabs, the pane's filter and counts, and the
/// debounced write-back. Anchoring rules live in <see cref="NoteAnchoring"/>; this file decides what
/// the user sees and when a note reaches the store.
/// </summary>
public partial class StudioViewModel
{
    private readonly Dictionary<string, NoteViewModel> _pendingNotes = new(StringComparer.Ordinal);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _noteSaveTimer;
    private NoteViewModel? _activeNote;
    private NoteRecord? _deletedNote;
    private TabViewModel? _deletedNoteTab;

    /// <summary>The notes the pane is showing, already filtered and sorted.</summary>
    public ObservableCollection<NoteViewModel> VisibleNotes { get; } = [];

    [ObservableProperty]
    public partial NoteScope NotesScope { get; set; }

    [ObservableProperty]
    public partial bool IsNotesPaneOpen { get; set; }

    public Visibility NotesPaneVisibility =>
        IsNotesPaneOpen && !IsActiveTabPoppedOut ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The pane's column width; 0 collapses it without reflowing the editor on every frame.</summary>
    public GridLength NotesPaneWidth =>
        IsNotesPaneOpen && !IsActiveTabPoppedOut ? new GridLength(336) : new GridLength(0);

    public int TabNoteCount { get; private set; }

    public int SpaceNoteCount { get; private set; }

    public int LostNoteCount { get; private set; }

    public string NotesCountLabel => VisibleNotes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Deleting a note is reversible in the status bar; a one-line note is easy to lose.</summary>
    public bool CanUndoNoteDelete => _deletedNote is not null && _deletedNoteTab is not null;

    public Visibility UndoNoteDeleteVisibility =>
        CanUndoNoteDelete ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NotesEmptyVisibility => VisibleNotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NotesListVisibility => VisibleNotes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string NotesEmptyTitle => NotesScope switch
    {
        NoteScope.Space => "No notes in this workspace",
        NoteScope.Unanchored => "Every note is anchored",
        _ => "No notes in this tab",
    };

    public string NotesEmptyDetail => NotesScope switch
    {
        NoteScope.Space => "Notes left on any tab in this workspace appear here.",
        NoteScope.Unanchored => "Nothing needs a new anchor.",
        _ => "Select a statement and press Ctrl+Alt+N.",
    };

    public string NotesSummary
    {
        get
        {
            var total = NotesScope == NoteScope.Space ? SpaceNoteCount : TabNoteCount;
            var lost = NotesScope == NoteScope.Space
                ? ActiveSpace?.Tabs.SelectMany(tab => tab.Notes).Count(note => note.State == NoteAnchorState.Lost) ?? 0
                : LostNoteCount;
            var anchored = Math.Max(0, total - lost);

            return lost == 0
                ? $"{anchored} anchored"
                : $"{anchored} anchored · {lost} need{(lost == 1 ? "s" : string.Empty)} attention";
        }
    }

    partial void OnNotesScopeChanged(NoteScope value) => RefreshNotes();

    partial void OnIsNotesPaneOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(NotesPaneVisibility));
        OnPropertyChanged(nameof(NotesPaneWidth));
        ScheduleSave();
    }

    /// <summary>
    /// Loads notes for every restored tab in one store read per workspace. Called once the tab list
    /// exists, because resolution needs each tab's saved content.
    /// </summary>
    private void LoadNotesFromStore()
    {
        foreach (var space in Spaces)
        {
            var byTab = _store
                .GetNotesForSpace(space.Id)
                .GroupBy(record => record.TabId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            foreach (var tab in space.Tabs)
            {
                if (byTab.TryGetValue(tab.Id, out var records))
                {
                    AddNotes(tab, records);
                }
            }
        }

        RefreshNotes();
    }

    /// <summary>
    /// Raised after notes are added, removed, restored or re-anchored. The shell owns the editor
    /// markers, so it refreshes them from this rather than from each individual command.
    /// </summary>
    public event EventHandler? NotesChanged;

    /// <summary>Loads the notes for one tab (a tab restored from the archive, or reopened by an agent).</summary>
    public void LoadNotesFor(TabViewModel tab)
    {
        AddNotes(tab, _store.GetNotes(tab.Id));
        RefreshNotes();
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddNotes(TabViewModel tab, IReadOnlyList<NoteRecord> records)
    {
        var existing = tab.Notes.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!existing.Add(record.Id))
            {
                continue;
            }

            var note = new NoteViewModel(record, tab);
            note.AnchorText = DescribeAnchor(note);
            note.PropertyChanged += Note_PropertyChanged;
            tab.Notes.Add(note);
        }
    }

    private void Note_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is NoteViewModel note
            && e.PropertyName is nameof(NoteViewModel.Body) or nameof(NoteViewModel.Kind))
        {
            ScheduleNoteSave(note);
        }
    }

    /// <summary>
    /// Creates a note on a captured range. The body starts empty on purpose: the editor opens
    /// focused and the first keystroke is the note, with no name to invent.
    /// </summary>
    public NoteViewModel CreateNote(TabViewModel tab, NoteAnchor anchor)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new NoteViewModel(
            new NoteRecord
            {
                Id = Guid.NewGuid().ToString("n"),
                SpaceId = tab.Space.Id,
                TabId = tab.Id,
                Kind = NoteKind.Note,
                Body = string.Empty,
                Anchor = anchor,
                CreatedAt = now,
                UpdatedAt = now,
            },
            tab);

        note.AnchorText = DescribeAnchor(note);
        note.PropertyChanged += Note_PropertyChanged;
        tab.Notes.Add(note);

        // The empty note is a real object immediately: closing the popover before typing must not
        // lose it, and the rail badge should show it at once.
        _store.SaveNote(note.ToRecord());

        IsNotesPaneOpen = true;
        SetActiveNote(note);
        RefreshNotes();
        NotesChanged?.Invoke(this, EventArgs.Empty);
        StatusText = "Note added";
        return note;
    }

    /// <summary>Deletes the note from the store and the tab. The status bar keeps one undo step.</summary>
    public void DeleteNote(NoteViewModel note)
    {
        _pendingNotes.Remove(note.Id);
        note.PropertyChanged -= Note_PropertyChanged;
        _store.DeleteNote(note.Id);
        note.Tab.Notes.Remove(note);

        if (ReferenceEquals(_activeNote, note))
        {
            _activeNote = null;
        }

        _deletedNote = note.ToRecord();
        _deletedNoteTab = note.Tab;
        NotifyUndoNoteDelete();

        RefreshNotes();
        NotesChanged?.Invoke(this, EventArgs.Empty);
        StatusText = "Note deleted";
    }

    [RelayCommand]
    private void UndoNoteDelete()
    {
        if (_deletedNote is not { } record || _deletedNoteTab is not { } tab)
        {
            return;
        }

        _deletedNote = null;
        _deletedNoteTab = null;
        NotifyUndoNoteDelete();

        if (!tab.Space.Tabs.Contains(tab))
        {
            // The tab left the workspace (archived or deleted); the note lives in the archive with it.
            StatusText = "The note's tab is no longer open";
            return;
        }

        var note = new NoteViewModel(record, tab);
        note.AnchorText = DescribeAnchor(note);
        note.PropertyChanged += Note_PropertyChanged;
        tab.Notes.Add(note);
        _store.SaveNote(note.ToRecord());

        RefreshNotes();
        NotesChanged?.Invoke(this, EventArgs.Empty);
        StatusText = "Note restored";
    }

    private void NotifyUndoNoteDelete()
    {
        OnPropertyChanged(nameof(CanUndoNoteDelete));
        OnPropertyChanged(nameof(UndoNoteDeleteVisibility));
    }

    /// <summary>Persists a changed body/kind after a short pause; the note edit surface calls this.</summary>
    public void ScheduleNoteSave(NoteViewModel note)
    {
        _pendingNotes[note.Id] = note;

        if (_noteSaveTimer is null)
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                SaveNoteNow(note);
                return;
            }

            _noteSaveTimer = queue.CreateTimer();
            _noteSaveTimer.Interval = TimeSpan.FromMilliseconds(450);
            _noteSaveTimer.IsRepeating = false;
            _noteSaveTimer.Tick += (_, _) => FlushPendingNotes();
        }

        _noteSaveTimer.Stop();
        _noteSaveTimer.Start();
    }

    public void SaveNoteNow(NoteViewModel note)
    {
        _pendingNotes.Remove(note.Id);
        _store.SaveNote(note.ToRecord());
    }

    private void FlushPendingNotes()
    {
        _noteSaveTimer?.Stop();
        if (_pendingNotes.Count == 0)
        {
            return;
        }

        foreach (var note in _pendingNotes.Values)
        {
            _store.SaveNote(note.ToRecord());
        }

        _pendingNotes.Clear();
    }

    /// <summary>
    /// Rebases every note on the tab across one edit and persists the anchors that moved. The shell
    /// calls this on the editor's text change with the document before and after.
    /// </summary>
    public void RebaseNotes(TabViewModel tab, string? oldText, string? newText)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return;
        }

        var stateChanged = false;
        foreach (var note in tab.Notes)
        {
            var before = note.Anchor;
            var stateBefore = note.State;
            note.Rebase(oldText, newText);

            if (!before.Equals(note.Anchor) || note.State == NoteAnchorState.Lost)
            {
                note.AnchorText = DescribeAnchor(note);
                ScheduleNoteSave(note);
            }

            stateChanged |= stateBefore != note.State;
        }

        // A note that just lost (or regained) its text changes which list it belongs to.
        if (stateChanged)
        {
            RefreshNotes();
        }
    }

    /// <summary>Points a lost note at a new range. The shell captures it from the editor.</summary>
    public void ReanchorNote(NoteViewModel note, NoteAnchor anchor)
    {
        note.Reanchor(anchor);
        note.AnchorText = DescribeAnchor(note);
        SaveNoteNow(note);
        RefreshNotes();
        NotesChanged?.Invoke(this, EventArgs.Empty);
        StatusText = "Note anchored";
    }

    /// <summary>Selects one note in the pane; the shell opens its editor.</summary>
    public void SetActiveNote(NoteViewModel? note)
    {
        if (!ReferenceEquals(_activeNote, note))
        {
            if (_activeNote is not null)
            {
                _activeNote.IsActive = false;
            }

            _activeNote = note;
        }

        if (note is not null)
        {
            note.IsActive = true;
        }
    }

    public NoteViewModel? ActiveNote => _activeNote;

    /// <summary>Recomputes the pane's list, counts and labels for the current scope and tab.</summary>
    public void RefreshNotes()
    {
        var tab = ActiveTab;
        var space = ActiveSpace;

        IEnumerable<NoteViewModel> source = NotesScope switch
        {
            NoteScope.Space => space is null ? [] : space.Tabs.SelectMany(candidate => candidate.Notes),
            NoteScope.Unanchored => tab is null
                ? []
                : tab.Notes.Where(note => note.State == NoteAnchorState.Lost),
            _ => tab?.Notes ?? [],
        };

        var tabOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        if (space is not null)
        {
            for (var index = 0; index < space.Tabs.Count; index++)
            {
                tabOrder[space.Tabs[index].Id] = index;
            }
        }

        var ordered = source
            .OrderBy(note => note.State == NoteAnchorState.Lost ? 1 : 0)
            .ThenBy(note => ReferenceEquals(note.Tab, tab) ? 0 : 1)
            .ThenBy(note => tabOrder.GetValueOrDefault(note.Tab.Id, int.MaxValue))
            .ThenBy(note => note.Anchor.Start)
            .ToList();

        foreach (var note in VisibleNotes)
        {
            note.IsActive = false;
        }

        VisibleNotes.Clear();
        foreach (var note in ordered)
        {
            note.AnchorText = DescribeAnchor(note);
            note.ShowTab = NotesScope == NoteScope.Space;
            note.IsActive = ReferenceEquals(note, _activeNote);
            VisibleNotes.Add(note);
        }

        TabNoteCount = tab?.NoteCount ?? 0;
        SpaceNoteCount = space?.Tabs.Sum(candidate => candidate.NoteCount) ?? 0;
        LostNoteCount = tab?.Notes.Count(note => note.State == NoteAnchorState.Lost) ?? 0;

        OnPropertyChanged(nameof(TabNoteCount));
        OnPropertyChanged(nameof(SpaceNoteCount));
        OnPropertyChanged(nameof(LostNoteCount));
        OnPropertyChanged(nameof(NotesCountLabel));
        OnPropertyChanged(nameof(NotesEmptyVisibility));
        OnPropertyChanged(nameof(NotesListVisibility));
        OnPropertyChanged(nameof(NotesEmptyTitle));
        OnPropertyChanged(nameof(NotesEmptyDetail));
        OnPropertyChanged(nameof(NotesSummary));
    }

    /// <summary>"Statement 2 · lines 12–21", or the truth when the text is gone.</summary>
    private static string DescribeAnchor(NoteViewModel note)
    {
        if (note.Anchor.IsTab)
        {
            return "This tab";
        }

        if (note.State == NoteAnchorState.Lost)
        {
            return "Anchor lost";
        }

        var text = note.Tab.Content;
        var start = Math.Clamp(note.Anchor.Start, 0, text.Length);
        var end = Math.Clamp(note.Anchor.Start + Math.Max(note.Anchor.Length, 1), start, text.Length);
        if (end <= start)
        {
            return "Line " + NoteAnchoring.LineOf(text, start).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var startLine = NoteAnchoring.LineOf(text, start);
        var endLine = NoteAnchoring.LineOf(text, Math.Max(start, end - 1));
        var lines = startLine == endLine
            ? $"Line {startLine}"
            : $"Lines {startLine}–{endLine}";

        var statements = note.Tab.Space.Connection.EditorLanguage.Split(text);
        var index = 0;
        foreach (var statement in statements)
        {
            if (statement.Start <= start)
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return index > 0 ? $"Statement {index} · {lines}" : lines;
    }
}
