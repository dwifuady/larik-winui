using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Data;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Larik.ViewModels;

/// <summary>
/// One vertical tab in the sidebar. A tab owns its database context and its last result, so
/// switching tabs swaps the completion schema, database picker and results grid together.
/// </summary>
public partial class TabViewModel : ObservableObject
{
    private bool _isPointerOver;

    public TabViewModel(string id, string title, string? database, SpaceViewModel space, string content)
    {
        Id = id;
        Space = space;
        Title = title;
        Database = database ?? string.Empty;
        Content = content;
        StatusText = "Ready";

        // Transactions, temp tables and SET options belong to the tab's own session, not to the
        // workspace. The session opens lazily so a restored tab costs nothing until it runs.
        Session = new QuerySession(space.Connection.CreateSession);
        Session.StateChanged += (_, _) => RaiseSessionStateChanged();

        // The workspace colour flows through to the tab strip and tint.
        space.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SpaceViewModel.ColorHex)
                or nameof(SpaceViewModel.AccentBrush)
                or nameof(SpaceViewModel.ActiveTintBrush))
            {
                OnPropertyChanged(nameof(AccentBrush));
                OnPropertyChanged(nameof(ActiveTintBrush));
            }
        };
    }

    public string Id { get; }

    public SpaceViewModel Space { get; }

    /// <summary>This tab's persistent server session; nothing is shared with the other tabs.</summary>
    public QuerySession Session { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    public bool IsRunning => Session.IsRunning;

    public bool CanRun => !Session.IsRunning;

    public Visibility IsRunningVisibility => Session.IsRunning ? Visibility.Visible : Visibility.Collapsed;

    public bool HasOpenTransaction => Session.HasOpenTransaction;

    public bool CanCommit => Session.CanCommit;

    public bool CanRollback => Session.CanRollback;

    public Visibility TransactionBadgeVisibility =>
        Session.HasOpenTransaction ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TransactionGuardVisibility => TransactionBadgeVisibility;

    public string TransactionSummary
    {
        get
        {
            if (!Session.HasOpenTransaction)
            {
                return string.Empty;
            }

            if (Session.IsDoomed)
            {
                return "Transaction is uncommittable — roll back";
            }

            return Session.Transaction.TransactionCount == 1
                ? "1 open transaction"
                : $"{Session.Transaction.TransactionCount} nested transactions open";
        }
    }

    public SolidColorBrush TransactionBrush => (SolidColorBrush)ThemeService.GetBrush(
        Session.IsDoomed ? "StudioDangerBrush" : "StudioWarningBrush",
        StudioColors.Warning);

    /// <summary>Active tabs carry primary text; inactive tabs recede (Arc sidebar behaviour).</summary>
    public Brush TitleBrush => ThemeService.GetBrush(
        IsActive ? "StudioTextPrimaryBrush" : "StudioTextSecondaryBrush",
        StudioColors.Muted);

    /// <summary>Code-resolved brushes do not follow a runtime theme switch on their own.</summary>
    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(TitleBrush));
        OnPropertyChanged(nameof(TransactionBrush));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(SubtitleVisibility))]
    public partial string Database { get; set; }

    [ObservableProperty]
    public partial string Content { get; set; }

    /// <summary>
    /// True while this tab is rendered in its own window. The tab keeps its rail row and stays the
    /// active tab (the shell shows a placeholder instead of a second editor); the pop-out owns the
    /// visible query surface. Not persisted: a restart opens every tab docked, as it always has.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PopoutGlyphVisibility))]
    [NotifyPropertyChangedFor(nameof(PopoutStatus))]
    public partial bool IsPoppedOut { get; set; }

    /// <summary>The rail marks a popped-out tab so the row does not look like a tab that vanished.</summary>
    public Visibility PopoutGlyphVisibility => IsPoppedOut ? Visibility.Visible : Visibility.Collapsed;

    public string PopoutStatus => IsPoppedOut ? "Open in its own window" : string.Empty;

    /// <summary>
    /// Pinned tabs are never removed by auto-archive. The flag survives a restart and a restore
    /// from the archive page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinVisibility))]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    [NotifyPropertyChangedFor(nameof(RowHeight))]
    public partial bool IsPinned { get; set; }

    /// <summary>
    /// The group this tab belongs to, or null for a loose tab. Pinning a tab and putting it in a
    /// pinned group are different: the tab's own pin is for loose tabs, a pinned group protects
    /// all of its members.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowHeight))]
    [NotifyPropertyChangedFor(nameof(RowPadding))]
    [NotifyPropertyChangedFor(nameof(IsInPinnedSection))]
    [NotifyPropertyChangedFor(nameof(PinReason))]
    public partial TabGroupViewModel? Group { get; set; }

    /// <summary>True when this tab renders in the pinned band, whether by its own pin or its group's.</summary>
    public bool IsInPinnedSection => IsPinned || Group?.IsPinned == true;

    /// <summary>Why this tab is protected from auto-archive; null when it is not.</summary>
    public string? PinReason => IsPinned ? "Pinned" : Group?.IsPinned == true ? $"Pinned in {Group.Name}" : null;

    /// <summary>Called when the group's pin changes: the tab's band and compact height follow it.</summary>
    public void NotifyGroupMembershipChanged()
    {
        OnPropertyChanged(nameof(IsInPinnedSection));
        OnPropertyChanged(nameof(RowHeight));
        OnPropertyChanged(nameof(RowPadding));
        OnPropertyChanged(nameof(PinReason));
    }

    /// <summary>True while the tab title is edited in place in the rail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleVisibility))]
    [NotifyPropertyChangedFor(nameof(RenameEditorVisibility))]
    public partial bool IsRenaming { get; set; }

    /// <summary>Working copy shown by the inline rename editor; only committed on Enter/lost focus.</summary>
    [ObservableProperty]
    public partial string DraftTitle { get; set; } = string.Empty;

    /// <summary>Last time this tab was selected or edited; auto-archive measures inactivity from it.</summary>
    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;

    public Visibility PinVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public string PinLabel => IsPinned ? "Unpin tab" : "Pin tab";

    public Visibility TitleVisibility => IsRenaming ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RenameEditorVisibility => IsRenaming ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Pinned rows sit above the TABS header and are a little more compact, like Arc.</summary>
    public double RowHeight => IsInPinnedSection ? 36 : 42;

    /// <summary>Grouped tabs indent under their header so the rail reads as a tree, not a list.</summary>
    public Thickness RowPadding => Group is null
        ? new Thickness(0, 0, 4, 0)
        : new Thickness(16, 0, 4, 0);

    public void BeginRename()
    {
        DraftTitle = Title;
        IsRenaming = true;
    }

    public void EndRename() => IsRenaming = false;

    /// <summary>Result tabs produced by this query tab, in execution order.</summary>
    public ObservableCollection<ResultSetViewModel> Results { get; } = [];

    private ResultSetViewModel? _activeResult;

    public ResultSetViewModel? ActiveResult => _activeResult;

    public QueryResult? Result => _activeResult?.Result;

    /// <summary>
    /// Replaces the active result tab when <paramref name="append"/> is false, or appends the new
    /// result tabs when it is true. A batch can produce several results at once.
    /// </summary>
    public void ApplyResults(IReadOnlyList<QueryResult> results, bool append)
    {
        if (results.Count == 0)
        {
            return;
        }

        var index = append || Results.Count == 0
            ? Results.Count
            : Math.Clamp(Results.IndexOf(_activeResult!), 0, Results.Count - 1);

        // Replacing the active result keeps its name: the new grid occupies the same tab.
        var replaced = !append && Results.Count > 0 ? Results[index] : null;
        if (replaced is not null)
        {
            Results.RemoveAt(index);
        }

        for (var i = 0; i < results.Count; i++)
        {
            // Carry the replaced tab's name only when the new result is the same kind (grid or
            // messages), so a message tab replaced by a grid never keeps the label "Messages".
            var carryName = i == 0
                && replaced is not null
                && (replaced.Result.Messages.Count > 0) == (results[i].Messages.Count > 0);
            var title = carryName ? replaced!.Title : NextResultTitle(results[i]);
            Results.Insert(index + i, new ResultSetViewModel(this, results[i], title));
        }

        SetActiveResult(Results[Math.Clamp(index, 0, Results.Count - 1)]);
    }

    /// <summary>
    /// Names are assigned once at creation: reordering must not renumber tabs, so a new result takes
    /// the next free number instead of its current position.
    /// </summary>
    private string NextResultTitle(QueryResult result)
    {
        if (result.Messages.Count > 0)
        {
            return "Messages";
        }

        var used = new HashSet<string>(Results.Select(item => item.Title), StringComparer.Ordinal);
        var number = 1;
        while (used.Contains($"Result {number}"))
        {
            number++;
        }

        return $"Result {number}";
    }

    public void SelectResult(ResultSetViewModel result)
    {
        if (Results.Contains(result))
        {
            SetActiveResult(result);
        }
    }

    /// <summary>
    /// Applies a reorder performed by the results strip. The active result stays active and every
    /// tab keeps its name; only the order changes.
    /// </summary>
    public void ApplyResultOrder(IReadOnlyList<ResultSetViewModel> order)
    {
        for (var target = 0; target < order.Count; target++)
        {
            var result = order[target];
            var current = Results.IndexOf(result);
            if (current >= 0 && current != target)
            {
                Results.Move(current, target);
            }
        }
    }

    public void CloseResult(ResultSetViewModel result)
    {
        var index = Results.IndexOf(result);
        if (index < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(_activeResult, result);
        Results.RemoveAt(index);

        if (Results.Count == 0)
        {
            SetActiveResult(null);
        }
        else if (wasActive)
        {
            SetActiveResult(Results[Math.Clamp(index, 0, Results.Count - 1)]);
        }
    }

    private void SetActiveResult(ResultSetViewModel? result)
    {
        foreach (var item in Results)
        {
            item.IsActive = item == result;
        }

        if (!ReferenceEquals(_activeResult, result))
        {
            _activeResult = result;
            OnPropertyChanged(nameof(ActiveResult));
            OnPropertyChanged(nameof(Result));
        }
    }

    /// <summary>Cancels any running query, rolls back an open transaction and disposes the session.</summary>
    public Task CloseSessionAsync(CancellationToken cancellationToken = default) =>
        Session.CloseAsync(cancellationToken);

    private void RaiseSessionStateChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(IsRunningVisibility));
        OnPropertyChanged(nameof(HasOpenTransaction));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(TransactionBadgeVisibility));
        OnPropertyChanged(nameof(TransactionGuardVisibility));
        OnPropertyChanged(nameof(TransactionSummary));
        OnPropertyChanged(nameof(TransactionBrush));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTintBrush))]
    [NotifyPropertyChangedFor(nameof(ActiveStripOpacity))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    [NotifyPropertyChangedFor(nameof(SubtitleVisibility))]
    [NotifyPropertyChangedFor(nameof(CloseButtonVisibility))]
    public partial bool IsActive { get; set; }

    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            if (SetProperty(ref _isPointerOver, value))
            {
                OnPropertyChanged(nameof(SubtitleVisibility));
                OnPropertyChanged(nameof(CloseButtonVisibility));
            }
        }
    }

    public string Subtitle => string.IsNullOrWhiteSpace(Database) ? Space.Name : Database;

    /// <summary>Database label reveals on hover or when the tab is active (Arc behaviour).</summary>
    public Visibility SubtitleVisibility => IsActive || IsPointerOver ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CloseButtonVisibility => IsActive || IsPointerOver ? Visibility.Visible : Visibility.Collapsed;

    public Brush? ActiveTintBrush => IsActive ? Space.ActiveTintBrush : null;

    public double ActiveStripOpacity => IsActive ? 1 : 0;

    public FontWeight TitleWeight => IsActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public SolidColorBrush AccentBrush => Space.AccentBrush;
}
