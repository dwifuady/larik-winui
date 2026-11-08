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

    public SolidColorBrush TransactionBrush => (SolidColorBrush)Application.Current.Resources[
        Session.IsDoomed ? "StudioDangerBrush" : "StudioWarningBrush"];

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
    /// Pinned tabs are never removed by auto-archive. The flag survives a restart and a restore
    /// from the archive page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinVisibility))]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    [NotifyPropertyChangedFor(nameof(RowHeight))]
    public partial bool IsPinned { get; set; }

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
    public double RowHeight => IsPinned ? 36 : 42;

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

        if (!append && Results.Count > 0)
        {
            Results.RemoveAt(index);
        }

        for (var i = 0; i < results.Count; i++)
        {
            Results.Insert(index + i, new ResultSetViewModel(this, results[i], index + i));
        }

        Reindex();
        SetActiveResult(Results[Math.Clamp(index, 0, Results.Count - 1)]);
    }

    public void SelectResult(ResultSetViewModel result)
    {
        if (Results.Contains(result))
        {
            SetActiveResult(result);
        }
    }

    /// <summary>
    /// Applies a reorder performed by the results strip. The active result stays active; only its
    /// position (and therefore its "Result N" label) changes.
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

        Reindex();
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
        Reindex();

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

    private void Reindex()
    {
        for (var index = 0; index < Results.Count; index++)
        {
            Results[index].Index = index;
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
