using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Larik.Data;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Xaml;

namespace Larik.ViewModels;

/// <summary>Shell state: workspaces, their connections/databases and tabs, and the active session.</summary>
public partial class StudioViewModel : ObservableObject
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StudioStore _store = new();
    private readonly SnippetLibrary _snippets = SnippetLibrary.Current;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _archiveTimer;
    private List<TabGroupViewModel> _lastGrouping = [];
    private int _queryCounter;
    private IReadOnlyList<TableSchema> _schema = [];
    private IReadOnlyList<RoutineSchema> _routines = [];

    // Schemas are per database; keeping them makes switching tabs back to a database instant.
    private readonly Dictionary<string, SchemaSnapshot> _schemaCache = new(StringComparer.OrdinalIgnoreCase);

    public StudioViewModel()
    {
        StatusText = "Ready";
        CaretText = "Ln 1, Col 1";
        ResultMessage = string.Empty;
        ResultSummary = string.Empty;

        if (_store.Load() is { Spaces.Count: > 0 } saved)
        {
            Restore(saved);
        }
        else
        {
            // A first run starts empty. The welcome surface owns the one setup decision, so the
            // app never ships someone else's sample workspaces into a real install.
            StatusText = "Create a workspace to get started";
        }

        // An empty rail during onboarding is only a 252 px blank column; hide it so the welcome
        // sits in the whole window. The first created workspace opens it again.
        IsSidebarOpen = HasSpaces;

        // The empty results pane names the next action. It only does so while the workspace is
        // disconnected: a hint that says "Connect…" next to a connected rail is dishonest.
        if (HasSpaces && ActiveSpace?.IsConnected != true)
        {
            ResultMessage = "Connect the workspace, then run a query with Ctrl+Enter";
        }

        RefreshArchiveView();

        Spaces.CollectionChanged += (_, _) =>
        {
            ScheduleSave();
            NotifyWorkspaceList();
        };
        _snippets.Changed += (_, _) => OnPropertyChanged(nameof(Snippets));
        ThemeService.Changed += (_, _) => NotifyThemeChanged();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ActiveSpace) or nameof(ActiveTab))
            {
                ScheduleSave();
            }
        };
    }

    public ObservableCollection<SpaceViewModel> Spaces { get; } = [];

    /// <summary>
    /// Last window rectangle (DIPs). The shell restores it at launch and updates it while the
    /// window moves; it travels with the session so the next launch opens where the user left it.
    /// </summary>
    public WindowBoundsState? WindowBounds { get; private set; }

    /// <summary>Last results-pane height, or null until the user resizes it.</summary>
    public double? ResultsPaneHeight { get; private set; }

    /// <summary>Last cell-preview width, or null for the default.</summary>
    public double? CellPreviewWidth { get; private set; }

    /// <summary>The appearance choice last applied to the window; persisted with the session.</summary>
    public AppTheme Theme { get; private set; } = AppTheme.System;

    /// <summary>Switches the live window and marks the preference for the next save.</summary>
    public void SetTheme(AppTheme theme)
    {
        Theme = theme;
        ThemeService.Apply(theme);
        ScheduleSave();
    }

    /// <summary>
    /// Re-evaluates view-model brushes that code resolved from the old theme. XAML
    /// <c>ThemeResource</c> values update on their own; these properties are plain CLR values.
    /// </summary>
    public void NotifyThemeChanged()
    {
        foreach (var space in Spaces)
        {
            foreach (var tab in space.Tabs)
            {
                tab.NotifyThemeChanged();
                foreach (var result in tab.Results)
                {
                    result.NotifyThemeChanged();
                }
            }
        }
    }

    public void SetWindowBounds(WindowBoundsState? bounds)
    {
        WindowBounds = bounds;
        ScheduleSave();
    }

    public void SetResultsPaneHeight(double height)
    {
        if (height <= 0 || Math.Abs((ResultsPaneHeight ?? 0) - height) < 0.5)
        {
            return;
        }

        ResultsPaneHeight = height;
        ScheduleSave();
    }

    public void SetCellPreviewWidth(double width)
    {
        if (width <= 0 || Math.Abs((CellPreviewWidth ?? 0) - width) < 0.5)
        {
            return;
        }

        CellPreviewWidth = width;
        ScheduleSave();
    }

    /// <summary>Tabs of the active space. Mutated in place so the ListView keeps its identity.</summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>
    /// The pinned band: pinned group headers and their tabs, then loose pinned tabs. Items are
    /// <see cref="TabGroupViewModel"/> or <see cref="TabViewModel"/>; the rail's template selector
    /// draws each one.
    /// </summary>
    public ObservableCollection<object> PinnedRail { get; } = [];

    /// <summary>The TABS band: unpinned group headers and their tabs, then loose tabs.</summary>
    public ObservableCollection<object> UnpinnedRail { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinnedTabsVisibility))]
    public partial bool HasPinnedTabs { get; set; }

    /// <summary>Shown only when at least two tabs share a database and are not grouped yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGroupByDatabaseVisibility))]
    public partial bool CanGroupByDatabase { get; set; }

    /// <summary>Undo stays available until the next rail action, not on a timer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoGroupingVisibility))]
    public partial bool CanUndoGrouping { get; set; }

    public Visibility PinnedTabsVisibility => HasPinnedTabs ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CanGroupByDatabaseVisibility =>
        CanGroupByDatabase ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UndoGroupingVisibility =>
        CanUndoGrouping ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Result tabs of the active query tab.</summary>
    public ObservableCollection<ResultSetViewModel> Results { get; } = [];

    /// <summary>Archived tabs, newest first. Backed by the <c>archived_tabs</c> table.</summary>
    public ObservableCollection<ArchivedTabViewModel> ArchivedTabs { get; } = [];

    /// <summary>The archive list after the history search filter is applied.</summary>
    public ObservableCollection<ArchivedTabViewModel> VisibleArchivedTabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveVisibility))]
    public partial bool IsArchiveOpen { get; set; }

    [ObservableProperty]
    public partial string ArchiveSearch { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveEmptyVisibility))]
    public partial bool HasVisibleArchivedTabs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasArchivedTabs))]
    [NotifyPropertyChangedFor(nameof(ArchiveClearVisibility))]
    public partial int ArchivedTabCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoArchiveDescription))]
    public partial bool AutoArchiveEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoArchiveDescription))]
    public partial double AutoArchiveDays { get; set; } = 30;

    public Visibility ArchiveVisibility => IsArchiveOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ArchiveEmptyVisibility => HasVisibleArchivedTabs ? Visibility.Collapsed : Visibility.Visible;

    public bool HasArchivedTabs => ArchivedTabCount > 0;

    public Visibility ArchiveClearVisibility => HasArchivedTabs ? Visibility.Visible : Visibility.Collapsed;

    public string ArchiveCountLabel => ArchivedTabCount == 1 ? "1 archived tab" : $"{ArchivedTabCount} archived tabs";

    public string AutoArchiveDescription => AutoArchiveEnabled
        ? $"Tabs untouched for {EffectiveAutoArchiveDays} days are archived automatically. Pinned tabs are never archived."
        : "Auto-archive is off. Pinned tabs are never archived.";

    /// <summary>NumberBox can report NaN while its text is cleared; fall back to the default.</summary>
    private int EffectiveAutoArchiveDays => double.IsFinite(AutoArchiveDays)
        ? Math.Clamp((int)Math.Round(AutoArchiveDays), 1, 3650)
        : 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultTabsVisibility))]
    [NotifyPropertyChangedFor(nameof(ResultHeaderVisibility))]
    public partial bool HasMultipleResults { get; set; }

    public Visibility ResultTabsVisibility => HasMultipleResults ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// With several results the tab strip already carries each result's row count and timing, so the
    /// extra RESULTS line is duplicate chrome. A single result has no tab strip, so the line stays.
    /// </summary>
    public Visibility ResultHeaderVisibility => HasMultipleResults ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarVisibility))]
    [NotifyPropertyChangedFor(nameof(ContextBadgeVisibility))]
    public partial bool IsSidebarOpen { get; set; }

    [ObservableProperty]
    public partial SpaceViewModel? ActiveSpace { get; set; }

    /// <summary>False on the first run and after the last workspace is deleted.</summary>
    public bool HasSpaces => Spaces.Count > 0;

    /// <summary>The content area is onboarding while no workspace exists.</summary>
    public Visibility WelcomeVisibility => HasSpaces ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Workspace-specific chrome (switcher, connection, tabs, editor) is hidden at onboarding.</summary>
    public Visibility WorkspaceChromeVisibility => HasSpaces ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Six icons fit in the rail footer at a legal hit-target size (26 px inside 172 px of usable
    /// width). Beyond that the footer becomes a position pill: ten unlabelled marks identify nothing,
    /// so the footer stops pretending to be a map and opens the labelled list instead.
    /// </summary>
    private const int FooterIconLimit = 6;

    /// <summary>
    /// The workspaces the rail and switcher offer: everything not hidden, plus the active one even
    /// when it is hidden. Keeping the open workspace visible avoids a "where am I?" state; it
    /// disappears as soon as the user switches away.
    /// </summary>
    public IReadOnlyList<SpaceViewModel> RailSpaces =>
        [.. Spaces.Where(space => !space.IsHidden || ReferenceEquals(space, ActiveSpace))];

    public int HiddenSpaceCount => Spaces.Count(space => space.IsHidden);

    public string HiddenSpaceCountLabel => HiddenSpaceCount == 1
        ? "1 hidden workspace"
        : $"{HiddenSpaceCount} hidden workspaces";

    public IReadOnlyList<SpaceViewModel> FooterSpaces => [.. RailSpaces.Take(FooterIconLimit)];

    public bool ShowsWorkspacePill => RailSpaces.Count > FooterIconLimit;

    public Visibility FooterIconsVisibility => ShowsWorkspacePill ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FooterPillVisibility => ShowsWorkspacePill ? Visibility.Visible : Visibility.Collapsed;

    public string WorkspacePositionText
    {
        get
        {
            var rail = RailSpaces;
            var index = ActiveSpace is null ? -1 : rail.ToList().IndexOf(ActiveSpace);
            return index < 0 ? string.Empty : $"{index + 1} / {rail.Count}";
        }
    }

    public bool CanGoToPreviousSpace => ActiveSpace is not null
        && RailSpaces.ToList().IndexOf(ActiveSpace) > 0;

    public bool CanGoToNextSpace
    {
        get
        {
            var rail = RailSpaces;
            var index = ActiveSpace is null ? -1 : rail.ToList().IndexOf(ActiveSpace);
            return index >= 0 && index < rail.Count - 1;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial TabViewModel? ActiveTab { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string CaretText { get; set; }

    [ObservableProperty]
    public partial string ResultMessage { get; set; }

    [ObservableProperty]
    public partial string ResultSummary { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public Visibility SidebarVisibility => IsSidebarOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The toolbar repeats the rail's database badge only when the rail is hidden; showing both at
    /// once is the same fact twice, which is what made the chrome feel noisy.
    /// </summary>
    public Visibility ContextBadgeVisibility => IsSidebarOpen ? Visibility.Collapsed : Visibility.Visible;

    public string WindowTitle => ActiveTab is null ? "Larik SQL Studio" : $"{ActiveTab.Title} — Larik";

    public IReadOnlyList<SnippetDef> Snippets =>
        _snippets.ForDialect(ActiveSpace?.Connection.Dialect ?? "mssql");

    /// <summary>
    /// Editor language of the active workspace, used for highlighting, statement runs and
    /// completion metadata. Falls back to generic SQL when there is no provider.
    /// </summary>
    public IEditorLanguage ActiveLanguage =>
        ActiveSpace?.Connection.EditorLanguage ?? GenericSqlLanguage.Default;

    /// <summary>Schema of the active database, used for completion. Empty until a connection opens.</summary>
    public IReadOnlyList<TableSchema> Tables => _schema;

    /// <summary>Procedures of the active database, used after EXEC and by the schema browser.</summary>
    public IReadOnlyList<RoutineSchema> Routines => _routines;

    public QueryResult? LastResult { get; private set; }

    public event EventHandler? ResultChanged;

    public event EventHandler? ActiveTabChanged;

    /// <summary>Raised when the user asks to format the active tab's SQL; the shell applies it.</summary>
    public event EventHandler? FormatRequested;

    /// <summary>The shell shows the database picker before creating a tab; the view owns dialogs.</summary>
    public event EventHandler? NewTabRequested;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    /// <summary>
    /// Raised when the active workspace cannot connect because its driver is not loaded. The page
    /// owns the install dialog, so the view model only names the missing provider.
    /// </summary>
    public event EventHandler<DriverInstallRequest>? DriverInstallRequested;

    /// <summary>A missing driver and the workspace that needs it.</summary>
    public sealed record DriverInstallRequest(string ProviderId, SpaceViewModel Space);

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (ActiveSpace is null || IsBusy)
        {
            return;
        }

        if (ActiveSpace.Connection.Provider is null)
        {
            if (!AddonPolicy.AddonsEnabled)
            {
                StatusText = "This driver isn't included in the Store edition. The GitHub build supports more databases.";
                return;
            }

            DriverInstallRequested?.Invoke(
                this,
                new DriverInstallRequest(ActiveSpace.Connection.ProviderId, ActiveSpace));
            return;
        }

        if (ActiveSpace.Connection.IsConnected)
        {
            if (ActiveSpace.Tabs.FirstOrDefault(tab => tab.IsRunning) is { } running)
            {
                StatusText = $"Stop the query in {running.Title} before disconnecting.";
                return;
            }

            var openTransactions = ActiveSpace.Tabs.Count(tab => tab.HasOpenTransaction);
            if (openTransactions > 0)
            {
                StatusText = openTransactions == 1
                    ? "Roll back the open transaction before disconnecting."
                    : $"Roll back the {openTransactions} open transactions before disconnecting.";
                return;
            }

            await DisconnectSpaceAsync(ActiveSpace);
        }
        else
        {
            await ConnectSpaceAsync(ActiveSpace);
        }
    }

    /// <summary>
    /// Re-resolves missing providers after a driver is installed while the app is running. The
    /// editor language and snippets follow the active space's connection.
    /// </summary>
    public void RefreshProviderAvailability()
    {
        foreach (var space in Spaces)
        {
            space.Connection.RefreshProvider();
        }

        OnPropertyChanged(nameof(ActiveLanguage));
        OnPropertyChanged(nameof(Snippets));
        OnPropertyChanged(nameof(ActiveSpace));
    }

    /// <summary>
    /// Asks the shell for a database choice. Creating a tab is a database decision, so it never
    /// happens straight from the command: the page shows the picker and calls <see cref="CreateTab"/>.
    /// </summary>
    [RelayCommand]
    private void NewTab()
    {
        if (ActiveSpace is not null)
        {
            NewTabRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------- archive + pinning

    [RelayCommand]
    private void OpenArchive()
    {
        RunAutoArchive();
        ApplyArchiveFilter();
        IsArchiveOpen = true;
    }

    [RelayCommand]
    private void CloseArchive() => IsArchiveOpen = false;

    [RelayCommand]
    private void ArchiveInactiveNow()
    {
        var count = RunAutoArchive();
        StatusText = count == 0
            ? "No inactive tabs to archive"
            : $"Archived {count} inactive tab{(count == 1 ? string.Empty : "s")}";
    }

    partial void OnArchiveSearchChanged(string value) => ApplyArchiveFilter();

    partial void OnAutoArchiveEnabledChanged(bool value) => ScheduleSave();

    partial void OnAutoArchiveDaysChanged(double value) => ScheduleSave();

    /// <summary>Renames a tab. An empty name is ignored so a tab never loses its label.</summary>
    public bool RenameTab(TabViewModel tab, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, tab.Title, StringComparison.Ordinal))
        {
            return false;
        }

        tab.Title = trimmed;
        StatusText = $"Renamed tab to {trimmed}";
        ScheduleSave();
        return true;
    }

    /// <summary>
    /// Pinning acts on what the user right-clicked: a loose tab, or the group a tab belongs to.
    /// A pinned group is the folder form, so the whole set moves band together.
    /// </summary>
    public void TogglePin(TabViewModel tab)
    {
        if (tab.Group is { } grouped)
        {
            ToggleGroupPin(grouped);
            return;
        }

        var space = tab.Space;
        if (space.Tabs.IndexOf(tab) < 0)
        {
            return;
        }

        ClearGroupUndo();
        tab.IsPinned = !tab.IsPinned;
        tab.LastAccessedAt = DateTimeOffset.UtcNow;

        // The rail puts pinned tabs first, so the persisted order follows the same rule.
        RebuildTabOrder(space);
        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabRail();
        }

        StatusText = tab.IsPinned ? $"Pinned {tab.Title}" : $"Unpinned {tab.Title}";
        ScheduleSave();
    }

    // ---------------------------------------------------------------- groups

    /// <summary>
    /// Creates one group per database shared by at least two loose tabs. Existing tab order is
    /// preserved; only headers appear, so nothing jumps while the user is working.
    /// </summary>
    [RelayCommand]
    private void GroupByDatabase()
    {
        if (ActiveSpace is not { } space)
        {
            return;
        }

        var taken = space.Groups
            .Select(group => group.Database)
            .Where(database => database is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var buckets = space.Tabs
            .Where(tab => !tab.IsPinned
                && tab.Group is null
                && !string.IsNullOrWhiteSpace(tab.Database)
                && !taken.Contains(tab.Database))
            .GroupBy(tab => tab.Database, StringComparer.OrdinalIgnoreCase)
            .Where(bucket => bucket.Count() >= 2)
            .ToList();

        if (buckets.Count == 0)
        {
            StatusText = "Nothing to group: tabs need to share a database";
            return;
        }

        ClearGroupUndo();
        _lastGrouping = [];
        var colorOffset = space.Groups.Count;

        foreach (var bucket in buckets)
        {
            var group = new TabGroupViewModel(
                Guid.NewGuid().ToString("n"),
                bucket.Key,
                NextGroupColor(colorOffset++),
                isPinned: false,
                database: bucket.Key)
            {
                // The working set stays open; the rest recede.
                IsExpanded = bucket.Contains(ActiveTab),
            };

            foreach (var tab in bucket)
            {
                AssignTab(tab, group);
            }

            space.Groups.Add(group);
            _lastGrouping.Add(group);
        }

        RebuildTabOrder(space);
        RefreshTabRail();
        EnableGroupUndo();
        ScheduleSave();

        var count = buckets.Sum(bucket => bucket.Count());
        StatusText = $"Grouped {count} tabs by database";
    }

    /// <summary>Removes the groups the last "Group by database" action created.</summary>
    [RelayCommand]
    private void UndoGrouping()
    {
        if (ActiveSpace is not { } space || _lastGrouping.Count == 0)
        {
            return;
        }

        foreach (var group in _lastGrouping.Where(space.Groups.Contains).ToList())
        {
            foreach (var tab in group.Tabs.ToList())
            {
                tab.Group = null;
                tab.IsPinned = false;
                tab.NotifyGroupMembershipChanged();
            }

            group.Tabs.Clear();
            space.Groups.Remove(group);
        }

        _lastGrouping = [];
        ClearGroupUndo();
        RebuildTabOrder(space);
        RefreshTabRail();
        ScheduleSave();
        StatusText = "Grouping undone";
    }

    public void ToggleGroupExpanded(TabGroupViewModel group)
    {
        group.IsExpanded = !group.IsExpanded;
        RefreshTabRail();
        ScheduleSave();
    }

    public void ToggleGroupPin(TabGroupViewModel group)
    {
        var space = group.Tabs.FirstOrDefault()?.Space ?? ActiveSpace;
        ClearGroupUndo();
        _lastGrouping = [];
        group.IsPinned = !group.IsPinned;

        foreach (var tab in group.Tabs)
        {
            // A grouped tab is protected by its group's pin, never by its own.
            tab.IsPinned = false;
            tab.NotifyGroupMembershipChanged();
        }

        if (space is not null)
        {
            RebuildTabOrder(space);
        }

        RefreshTabRail();
        ScheduleSave();
        StatusText = group.IsPinned
            ? $"Pinned group {group.Name}"
            : $"Unpinned group {group.Name}";
    }

    public bool RenameGroup(TabGroupViewModel group, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, group.Name, StringComparison.Ordinal))
        {
            return false;
        }

        group.Name = trimmed;
        StatusText = $"Renamed group to {trimmed}";
        ScheduleSave();
        return true;
    }

    /// <summary>Creates a group around one tab. A pinned tab therefore becomes a pinned folder.</summary>
    public void NewGroupFromTab(TabViewModel tab)
    {
        var space = tab.Space;
        ClearGroupUndo();
        _lastGrouping = [];
        var name = !string.IsNullOrWhiteSpace(tab.Database) ? tab.Database : "New group";

        var group = new TabGroupViewModel(
            Guid.NewGuid().ToString("n"),
            name,
            NextGroupColor(space.Groups.Count),
            isPinned: tab.IsPinned,
            database: string.IsNullOrWhiteSpace(tab.Database) ? null : tab.Database)
        {
            IsExpanded = true,
        };

        AssignTab(tab, group);
        space.Groups.Add(group);
        RebuildTabOrder(space);
        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabRail();
        }

        ScheduleSave();
        StatusText = $"Created group {name}";
    }

    /// <summary>Moves a tab into a group, or to the loose tabs when <paramref name="target"/> is null.</summary>
    public void MoveTabToGroup(TabViewModel tab, TabGroupViewModel? target)
    {
        if (ReferenceEquals(tab.Group, target))
        {
            return;
        }

        ClearGroupUndo();
        _lastGrouping = [];
        var space = tab.Space;
        var wasPinned = tab.Group?.IsPinned ?? tab.IsPinned;

        if (target is null)
        {
            tab.Group?.Tabs.Remove(tab);
            tab.Group = null;

            // Keep the tab in the band it was already in; ungrouping should not teleport it.
            tab.IsPinned = wasPinned;
            tab.NotifyGroupMembershipChanged();
        }
        else
        {
            AssignTab(tab, target);
            target.IsExpanded = true;
        }

        PruneEmptyGroups(space);
        RebuildTabOrder(space);
        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabRail();
        }

        ScheduleSave();
        StatusText = target is null
            ? $"Moved {tab.Title} to loose tabs"
            : $"Moved {tab.Title} to {target.Name}";
    }

    /// <summary>Dissolves a group; its tabs stay where they are as loose tabs in the same band.</summary>
    public void Ungroup(TabGroupViewModel group)
    {
        var space = group.Tabs.FirstOrDefault()?.Space ?? ActiveSpace;
        if (space is null)
        {
            return;
        }

        var name = group.Name;
        var pinned = group.IsPinned;
        var tabs = group.Tabs.ToList();

        ClearGroupUndo();
        _lastGrouping = [];

        foreach (var tab in tabs)
        {
            tab.Group = null;
            tab.IsPinned = pinned;
            tab.NotifyGroupMembershipChanged();
        }

        group.Tabs.Clear();
        space.Groups.Remove(group);
        _lastGrouping.Remove(group);

        RebuildTabOrder(space);
        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabRail();
        }

        ScheduleSave();
        StatusText = $"Ungrouped {name}";
    }

    private void AssignTab(TabViewModel tab, TabGroupViewModel group)
    {
        if (ReferenceEquals(tab.Group, group))
        {
            return;
        }

        tab.Group?.Tabs.Remove(tab);
        if (!group.Tabs.Contains(tab))
        {
            group.Tabs.Add(tab);
        }

        tab.Group = group;
        tab.IsPinned = false;
        tab.NotifyGroupMembershipChanged();
    }

    private static void PruneEmptyGroups(SpaceViewModel space)
    {
        foreach (var empty in space.Groups.Where(group => group.Tabs.Count == 0).ToList())
        {
            space.Groups.Remove(empty);
        }
    }

    private static string NextGroupColor(int index) =>
        StudioColors.Palette[Math.Abs(index) % StudioColors.Palette.Length];

    /// <summary>
    /// Undo stays available until the next rail action. A timer would make the one way back from
    /// grouping disappear while the user is still reading the result.
    /// </summary>
    private void EnableGroupUndo() => CanUndoGrouping = true;

    private void ClearGroupUndo() => CanUndoGrouping = false;

    /// <summary>
    /// Moves a tab out of its workspace into the archive. Auto-archive always leaves one tab in
    /// the workspace; closing a tab explicitly may archive the last one, and the caller replaces it.
    /// </summary>
    public bool ArchiveTab(TabViewModel tab, bool notify = true, bool allowLast = false)
    {
        var space = tab.Space;
        if (!allowLast && space.Tabs.Count <= 1)
        {
            if (notify)
            {
                StatusText = "A workspace keeps at least one tab";
            }

            return false;
        }

        var index = space.Tabs.IndexOf(tab);
        if (index < 0)
        {
            return false;
        }

        ClearGroupUndo();
        _lastGrouping = [];
        var wasActive = ReferenceEquals(ActiveTab, tab);

        // Closing/archiving disposes the tab's own server session. CloseTabAsync already awaited
        // this; a second call is a no-op. Auto-archive reaches here without an await, so this
        // best-effort close keeps the connection (and any transaction) from leaking.
        tab.Session.Cancel();
        _ = tab.CloseSessionAsync(CancellationToken.None);

        var entry = new ArchivedTabViewModel(
            tab.Id,
            space.Id,
            space.Name,
            space.ColorHex,
            tab.Title,
            tab.Database,
            tab.Content,
            tab.IsPinned || tab.Group?.IsPinned == true,
            DateTimeOffset.UtcNow,
            tab.Group?.Name);

        ArchivedTabs.Insert(0, entry);
        if (ReferenceEquals(space.LastActiveTab, tab))
        {
            space.LastActiveTab = null;
        }

        tab.Group?.Tabs.Remove(tab);
        tab.Group = null;
        space.Tabs.Remove(tab);
        RebuildTabOrder(space);

        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabRail();
        }

        if (wasActive && ReferenceEquals(space, ActiveSpace) && space.Tabs.Count > 0)
        {
            SelectTab(space.Tabs[Math.Clamp(index, 0, space.Tabs.Count - 1)]);
        }

        RefreshArchiveView();
        ScheduleSave();

        if (notify)
        {
            StatusText = $"Moved {tab.Title} to archive";
        }

        return true;
    }

    public void RestoreArchived(ArchivedTabViewModel archived)
    {
        if (!ArchivedTabs.Remove(archived))
        {
            return;
        }

        ClearGroupUndo();
        _lastGrouping = [];

        var space = Spaces.FirstOrDefault(candidate => candidate.Id == archived.SpaceId)
            ?? ActiveSpace
            ?? Spaces.FirstOrDefault();

        if (space is null)
        {
            ArchivedTabs.Insert(0, archived);
            return;
        }

        var tab = new TabViewModel(
            archived.Id,
            archived.Title,
            archived.Database,
            space,
            archived.Content)
        {
            IsPinned = archived.IsPinned,
            LastAccessedAt = DateTimeOffset.UtcNow,
        };

        // A group with the same name (and band) is the remembered home; otherwise the tab comes
        // back as a loose tab, still pinned if it was.
        var group = string.IsNullOrWhiteSpace(archived.GroupName)
            ? null
            : space.Groups.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, archived.GroupName, StringComparison.OrdinalIgnoreCase)
                && candidate.IsPinned == archived.IsPinned)
                ?? space.Groups.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, archived.GroupName, StringComparison.OrdinalIgnoreCase));

        if (group is not null)
        {
            tab.IsPinned = false;
            tab.Group = group;
            group.Tabs.Add(tab);
            group.IsExpanded = true;
            space.Tabs.Add(tab);
        }
        else
        {
            space.Tabs.Insert(archived.IsPinned ? 0 : LooseInsertIndex(space), tab);
        }

        RebuildTabOrder(space);

        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabRail();
            SelectTab(tab);
        }

        RefreshArchiveView();
        ScheduleSave();
        StatusText = $"Restored {archived.Title} to {space.Name}";
    }

    public void DeleteArchived(ArchivedTabViewModel archived)
    {
        if (ArchivedTabs.Remove(archived))
        {
            RefreshArchiveView();
            ScheduleSave();
            StatusText = $"Deleted archived tab {archived.Title}";
        }
    }

    public void ClearArchive()
    {
        if (ArchivedTabs.Count == 0)
        {
            return;
        }

        var count = ArchivedTabs.Count;
        ArchivedTabs.Clear();
        RefreshArchiveView();
        ScheduleSave();
        StatusText = $"Cleared {count} archived tab{(count == 1 ? string.Empty : "s")}";
    }

    /// <summary>
    /// Runs the inactivity sweep. Pinned tabs and the tab the user is looking at are never
    /// archived, and a workspace always keeps at least one tab.
    /// </summary>
    public int RunAutoArchive()
    {
        if (!AutoArchiveEnabled)
        {
            return 0;
        }

        var days = EffectiveAutoArchiveDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var archived = 0;

        foreach (var space in Spaces.ToList())
        {
            foreach (var tab in space.Tabs.ToList())
            {
                if (space.Tabs.Count <= 1)
                {
                    break;
                }

                if (tab.IsPinned
                    || tab.Group?.IsPinned == true
                    || ReferenceEquals(tab, ActiveTab)
                    || tab.LastAccessedAt > cutoff)
                {
                    continue;
                }

                if (ArchiveTab(tab, notify: false))
                {
                    archived++;
                }
            }
        }

        if (archived > 0)
        {
            RefreshArchiveView();
            ScheduleSave();
            StatusText = $"Archived {archived} inactive tab{(archived == 1 ? string.Empty : "s")}";
        }

        return archived;
    }

    /// <summary>Starts the launch sweep and the hourly inactivity check.</summary>
    public void StartBackgroundMaintenance()
    {
        RunAutoArchive();

        if (_archiveTimer is not null)
        {
            return;
        }

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        _archiveTimer = queue.CreateTimer();
        _archiveTimer.Interval = TimeSpan.FromHours(1);
        _archiveTimer.IsRepeating = true;
        _archiveTimer.Tick += (_, _) => RunAutoArchive();
        _archiveTimer.Start();
    }

    private void RefreshArchiveView()
    {
        ArchivedTabCount = ArchivedTabs.Count;
        ApplyArchiveFilter();
    }

    private void ApplyArchiveFilter()
    {
        var query = ArchiveSearch?.Trim();

        VisibleArchivedTabs.Clear();
        foreach (var tab in ArchivedTabs)
        {
            if (string.IsNullOrEmpty(query)
                || tab.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tab.SpaceName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tab.Database.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tab.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisibleArchivedTabs.Add(tab);
            }
        }

        HasVisibleArchivedTabs = VisibleArchivedTabs.Count > 0;
    }

    /// <summary>Creates and activates a tab bound to <paramref name="databaseName"/> (null = unbound).</summary>
    public TabViewModel CreateTab(string? databaseName)
    {
        var space = ActiveSpace ?? throw new InvalidOperationException("No active workspace.");
        ClearGroupUndo();

        var tab = new TabViewModel(
            Guid.NewGuid().ToString("n"),
            $"Query {++_queryCounter}",
            databaseName ?? string.Empty,
            space,
            string.Empty);

        // After "Group by database", a tab opened on that database lands in its group; the group
        // expands so the new tab is visible instead of hidden inside a collapsed header.
        var group = string.IsNullOrWhiteSpace(databaseName)
            ? null
            : space.Groups.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Database)
                && string.Equals(candidate.Database, databaseName, StringComparison.OrdinalIgnoreCase));

        if (group is not null)
        {
            tab.Group = group;
            group.Tabs.Add(tab);
            group.IsExpanded = true;
            space.Tabs.Add(tab);
        }
        else
        {
            // A new tab opens above the other loose tabs, like Arc's new-tab row.
            space.Tabs.Insert(LooseInsertIndex(space), tab);
        }

        RebuildTabOrder(space);
        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabRail();
        }

        SelectTab(tab);
        StatusText = string.IsNullOrWhiteSpace(databaseName)
            ? $"New query in {space.Name} · no database"
            : $"New query in {space.Name} · {databaseName}";
        return tab;
    }

    /// <summary>
    /// Creates a workspace from the create dialog. Nothing is created until the user confirms;
    /// the caller connects the returned space afterwards.
    /// </summary>
    public SpaceViewModel CreateSpace(string name, string colorHex, string? iconName, string providerId, ConnectionProfile profile)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? $"Space {Spaces.Count + 1}" : name.Trim();
        var color = string.IsNullOrWhiteSpace(colorHex)
            ? StudioColors.Palette[Spaces.Count % StudioColors.Palette.Length]
            : colorHex;

        var space = new SpaceViewModel(
            Guid.NewGuid().ToString("n"),
            displayName,
            color,
            new ConnectionViewModel(providerId, profile),
            string.IsNullOrWhiteSpace(iconName) ? null : iconName);

        space.Tabs.Add(new TabViewModel(Guid.NewGuid().ToString("n"), "Query 1", string.Empty, space, string.Empty));

        PersistSecrets(space);
        TrackSpace(space);
        var firstSpace = !HasSpaces;
        Spaces.Add(space);
        if (firstSpace)
        {
            // Creating the first workspace is the moment the rail becomes useful.
            IsSidebarOpen = true;
        }

        SelectSpace(space);
        ScheduleSave();
        return space;
    }

    /// <summary>
    /// Deletes a workspace. Tabs that hold SQL move to the archive instead of being destroyed, so
    /// the action never loses the user's work; connection secrets are removed with the profile.
    /// The caller confirms the action because removing the workspace itself is not reversible.
    /// </summary>
    public async Task DeleteSpaceAsync(SpaceViewModel space)
    {
        if (!Spaces.Contains(space))
        {
            return;
        }

        if (space.Tabs.FirstOrDefault(tab => tab.IsRunning) is { } running)
        {
            StatusText = $"Stop the query in {running.Title} before deleting {space.Name}.";
            return;
        }

        if (space.Tabs.Any(tab => tab.HasOpenTransaction))
        {
            StatusText = $"Roll back the open transaction before deleting {space.Name}.";
            return;
        }

        var index = Spaces.IndexOf(space);

        // The archive is the safety net: any tab with SQL survives as a restorable history entry.
        var savedTabs = space.Tabs
            .Where(tab => !string.IsNullOrWhiteSpace(tab.Content))
            .ToList();

        foreach (var tab in savedTabs)
        {
            ArchivedTabs.Insert(0, new ArchivedTabViewModel(
                tab.Id,
                space.Id,
                space.Name,
                space.ColorHex,
                tab.Title,
                tab.Database,
                tab.Content,
                tab.IsPinned,
                DateTimeOffset.UtcNow));
        }

        // Each tab owns one session; close them before the connection and profile disappear.
        foreach (var tab in space.Tabs)
        {
            tab.Session.Cancel();
            await tab.CloseSessionAsync(CancellationToken.None);
        }

        DeleteSecrets(space, space.Connection.Provider);
        await space.Connection.DisposeAsync();

        var wasActive = ReferenceEquals(space, ActiveSpace);
        Spaces.Remove(space);

        if (wasActive)
        {
            if (Spaces.Count > 0)
            {
                SelectSpace(Spaces[Math.Clamp(index, 0, Spaces.Count - 1)]);
            }
            else
            {
                ClearActiveWorkspace();
            }
        }

        RefreshArchiveView();
        ScheduleSave();

        var archived = savedTabs.Count == 1 ? "1 tab" : $"{savedTabs.Count} tabs";
        StatusText = Spaces.Count == 0
            ? "Create a workspace to get started"
            : savedTabs.Count == 0
                ? $"Deleted {space.Name}"
                : $"Deleted {space.Name} · {archived} moved to archive";
    }

    /// <summary>Returns the shell to the first-run welcome surface once the last workspace is gone.</summary>
    private void ClearActiveWorkspace()
    {
        ActiveSpace = null;
        ActiveTab = null;
        Tabs.Clear();
        PinnedRail.Clear();
        UnpinnedRail.Clear();
        HasPinnedTabs = false;
        CanGroupByDatabase = false;
        CanUndoGrouping = false;
        _lastGrouping = [];
        Results.Clear();
        LastResult = null;
        ResultMessage = string.Empty;
        ResultSummary = string.Empty;
        HasMultipleResults = false;
        ClearSchema();
        IsSidebarOpen = false;
        StatusText = "Create a workspace to get started";
        OnPropertyChanged(nameof(Snippets));
        OnPropertyChanged(nameof(ActiveLanguage));
        OnPropertyChanged(nameof(ActiveSpace));
        OnPropertyChanged(nameof(WindowTitle));
        NotifyWorkspaceFooter();
    }

    /// <summary>
    /// Saves a workspace as one object: its name, colour and connection (a workspace owns exactly
    /// one connection, so editing one is editing the other). The connection is rebuilt only when it
    /// actually changed, so renaming never drops a live session; when it did change, the workspace
    /// on screen reconnects. Returns null on success, or the reason the edit was refused.
    /// </summary>
    public async Task<string?> ApplyWorkspaceSettingsAsync(
        SpaceViewModel space,
        string name,
        string colorHex,
        string? iconName,
        string providerId,
        ConnectionProfile profile)
    {
        RenameSpace(space, name);
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            space.ColorHex = colorHex;
        }

        space.IconName = string.IsNullOrWhiteSpace(iconName) ? null : iconName;

        var connectionChanged = !ConnectionMatches(space.Connection, providerId, profile);
        var isActive = ReferenceEquals(space, ActiveSpace);

        if (connectionChanged)
        {
            if (space.Tabs.Any(tab => tab.IsRunning))
            {
                return "Stop the running query before changing the connection.";
            }

            if (space.Tabs.Any(tab => tab.HasOpenTransaction))
            {
                return "Roll back open transactions before changing the connection.";
            }

            // Existing tab sessions belong to the old profile; close them before the profile swaps.
            foreach (var tab in space.Tabs)
            {
                await tab.CloseSessionAsync(CancellationToken.None);
            }

            var previousProvider = space.Connection.Provider;
            var previousProviderId = space.Connection.ProviderId;

            await space.Connection.ReconfigureAsync(providerId, profile);
            if (!string.Equals(previousProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                DeleteSecrets(space, previousProvider);
            }

            PersistSecrets(space);
            space.UpdateDatabases([]);
            _schemaCache.Clear();
            StatusText = $"{space.Name} → {space.Connection.ProviderName}";
        }

        ScheduleSave();

        if (!isActive)
        {
            // Editing a workspace you are not in must not change what is on screen, so it stays
            // disconnected until it is selected and connected from there.
            return null;
        }

        if (connectionChanged)
        {
            ClearSchema();
            OnPropertyChanged(nameof(Snippets));
            OnPropertyChanged(nameof(ActiveLanguage));

            // New connection settings imply using them: the workspace on screen reconnects.
            await ConnectSpaceAsync(space);
        }

        return null;
    }

    /// <summary>
    /// Fills the provider's password fields from the credential store so the settings form shows the
    /// value the connection will actually use; an untouched password is then saved unchanged.
    /// </summary>
    public void PrepareConnectionEdit(SpaceViewModel space) => LoadSecrets(space);

    /// <summary>True when the connection already holds exactly this provider and profile.</summary>
    private static bool ConnectionMatches(ConnectionViewModel connection, string providerId, ConnectionProfile profile)
    {
        if (!string.Equals(connection.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            || connection.Profile.Values.Count != profile.Values.Count)
        {
            return false;
        }

        return profile.Values.All(pair =>
            connection.Profile.Values.TryGetValue(pair.Key, out var current)
            && string.Equals(current, pair.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs SQL the shell resolved from the editor (a selection, the statement under the caret,
    /// or the whole document) on the active tab. The tab pins the database, its own session and
    /// the target result, so switching tabs or spaces while the query runs cannot retarget it.
    /// </summary>
    public Task RunSqlAsync(string sql, bool append = false) => RunBatchesAsync([sql], append);

    /// <summary>
    /// Runs a reference-preview lookup on the active tab's own session without touching the query
    /// tab's results or status. The session keeps temp tables and transaction context, which is what
    /// a reference to a temp/user table needs.
    /// </summary>
    public async Task<QueryResult> RunCellReferenceQueryAsync(
        string sql,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var tab = ActiveTab;
        var space = ActiveSpace;
        if (tab is null || space?.Connection.IsConnected != true)
        {
            return QueryResult.Error("Connect the workspace to preview referenced rows.", TimeSpan.Zero);
        }

        var database = DatabaseNameFor(space, tab);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var results = await tab.Session.ExecuteBatchAsync(
            sql,
            database,
            new QueryExecutionOptions { MaxRows = maxRows },
            linked.Token);

        return results.FirstOrDefault(result => !result.IsError && result.Columns.Count > 0)
               ?? results.FirstOrDefault(result => result.IsError)
               ?? QueryResult.Error("No reference rows returned.", TimeSpan.Zero, database);
    }

    /// <summary>A row-count estimate for choosing list vs lookup; null when the provider cannot tell.</summary>
    public async Task<long?> EstimateRowCountAsync(
        string? schema,
        string table,
        CancellationToken cancellationToken)
    {
        var tab = ActiveTab;
        var space = ActiveSpace;
        if (tab is null || space?.Connection.IsConnected != true)
        {
            return null;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            return await tab.Session.EstimateRowCountAsync(
                DatabaseNameFor(space, tab),
                schema,
                table,
                linked.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the reference resolver for a result. Null when the active provider has no SQL
    /// dialect, because building a safe lookup is the dialect's job.
    /// </summary>
    public ICellReferenceController? CreateCellReferenceController(QueryResult result)
    {
        if (ActiveLanguage is not ISqlDialect dialect)
        {
            return null;
        }

        return new CellReferenceController(
            result,
            string.IsNullOrWhiteSpace(result.Statement) ? ActiveTab?.Content : result.Statement,
            Tables,
            dialect,
            RunCellReferenceQueryAsync,
            EstimateRowCountAsync);
    }

    /// <summary>
    /// Runs one or more GO-delimited batches in order on the tab's own session. Semicolons stay
    /// inside a batch, so declared variables and temp tables keep their scope exactly as they do
    /// when SQL Server receives a native batch; <c>GO</c> starts a new server batch.
    /// </summary>
    public async Task RunBatchesAsync(IReadOnlyList<string> batches, bool append = false)
    {
        var tab = ActiveTab;
        var space = ActiveSpace;
        if (tab is null || space is null)
        {
            return;
        }

        if (IsBusy)
        {
            StatusText = "A connection change is in progress.";
            return;
        }

        if (tab.Session.IsRunning)
        {
            tab.StatusText = "This tab is already running a query.";
            if (ReferenceEquals(tab, ActiveTab))
            {
                StatusText = tab.StatusText;
            }

            return;
        }

        var executable = batches
            .Where(batch => !string.IsNullOrWhiteSpace(batch))
            .ToList();

        if (executable.Count == 0)
        {
            tab.StatusText = "Nothing to run";
            if (ReferenceEquals(tab, ActiveTab))
            {
                StatusText = tab.StatusText;
            }

            return;
        }

        if (!space.Connection.IsConnected)
        {
            ApplyResults(tab, [QueryResult.Error(
                $"Not connected — connect to {space.Name} to run queries.",
                TimeSpan.Zero)], append, "Disconnected");
            return;
        }

        if (executable.Any(HasUnguardedMutation))
        {
            ApplyResults(tab, [QueryResult.Error(
                "Refused: this statement modifies rows without a WHERE clause.",
                TimeSpan.Zero)], append, "Error");
            return;
        }

        tab.StatusText = "Running…";
        if (ReferenceEquals(tab, ActiveTab))
        {
            StatusText = tab.StatusText;
        }

        try
        {
            var results = new List<QueryResult>();
            var database = DatabaseNameFor(space, tab);
            for (var index = 0; index < executable.Count; index++)
            {
                // Only the first batch is re-targeted: a USE in one GO batch must carry into the
                // next, exactly as it does in an SSMS query window. A new run resets the session
                // to the tab's database again.
                // The provider applies the row cap while reading and returns one result per
                // result set. Each session is the tab's own, including its transaction.
                var batchResults = await tab.Session.ExecuteBatchAsync(
                    executable[index],
                    index == 0 ? database : null,
                    QueryExecutionOptions.Default,
                    _lifetime.Token);

                results.AddRange(batchResults);

                // A GO-delimited script stops at the first failed batch, like the native batch
                // does inside SQL Server. The tab keeps the error and whatever succeeded before it.
                if (batchResults.Any(result => result.IsError))
                {
                    break;
                }
            }

            ApplyResults(tab, results, append);

            // A USE inside the script changed the session's database. Adopt it as the tab's database,
            // so the next run does not switch the session back and completion/picker follow.
            await AdoptCurrentDatabaseAsync(tab, space);
        }
        catch (OperationCanceledException)
        {
            ApplyResults(tab, [QueryResult.Error("Cancelled.", TimeSpan.Zero)], append, "Cancelled");
        }
        catch (Exception ex)
        {
            ApplyResults(tab, [QueryResult.Error(ex.Message, TimeSpan.Zero)], append);
        }
    }

    [RelayCommand]
    private async Task CommitTransactionAsync()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        try
        {
            await tab.Session.CommitAsync(_lifetime.Token);
            tab.StatusText = "Transaction committed";
        }
        catch (Exception ex)
        {
            tab.StatusText = $"Commit failed: {ex.Message}";
        }

        if (ReferenceEquals(tab, ActiveTab))
        {
            StatusText = tab.StatusText;
        }
    }

    [RelayCommand]
    private async Task RollbackTransactionAsync()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        try
        {
            await tab.Session.RollbackAsync(_lifetime.Token);
            tab.StatusText = "Transaction rolled back";
        }
        catch (Exception ex)
        {
            tab.StatusText = $"Rollback failed: {ex.Message}";
        }

        if (ReferenceEquals(tab, ActiveTab))
        {
            StatusText = tab.StatusText;
        }
    }

    [RelayCommand]
    private void CancelRun()
    {
        if (ActiveTab is not { } tab || !tab.IsRunning)
        {
            return;
        }

        tab.Session.Cancel();
        tab.StatusText = "Cancelling…";
        StatusText = tab.StatusText;
    }

    /// <summary>The deliberately conservative safety net, applied per GO batch.</summary>
    private static bool HasUnguardedMutation(string sql)
    {
        var upper = sql.ToUpperInvariant();
        var mutates = upper.Contains("UPDATE ") || upper.Contains("DELETE ") || upper.Contains("TRUNCATE ");
        return mutates && !upper.Contains("WHERE");
    }

    [RelayCommand]
    private void Format() => FormatRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Stores new result tabs on their query tab and mirrors the active one to the shell.</summary>
    private void ApplyResults(
        TabViewModel tab,
        IReadOnlyList<QueryResult> results,
        bool append,
        string? status = null)
    {
        tab.ApplyResults(results, append);
        tab.StatusText = status ?? DescribeRun(results, append);

        if (!ReferenceEquals(tab, ActiveTab))
        {
            return;
        }

        SyncResultsMirror(tab);
        StatusText = tab.StatusText;
        ResultChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a result tab and republishes its grid, message and summary.</summary>
    public void SelectResult(ResultSetViewModel result)
    {
        if (ActiveTab is null || !ReferenceEquals(result.Tab, ActiveTab))
        {
            return;
        }

        ActiveTab.SelectResult(result);
        SyncResultsMirror(ActiveTab);
        ActiveTab.StatusText = result.Result.IsError
            ? "Error"
            : result.Result.Rows.Count > 0
                ? $"{result.Result.Rows.Count} rows returned"
                : result.Result.Message;
        StatusText = ActiveTab.StatusText;
        ResultChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes one result tab and keeps the remaining active result/shell mirror coherent.</summary>
    public void CloseResult(ResultSetViewModel result)
    {
        if (ActiveTab is null || !ReferenceEquals(result.Tab, ActiveTab))
        {
            return;
        }

        ActiveTab.CloseResult(result);
        SyncResultsMirror(ActiveTab);
        ActiveTab.StatusText = ActiveTab.ActiveResult is not { } active
            ? "No results"
            : active.Result.IsError
                ? "Error"
                : active.Result.Rows.Count > 0
                    ? $"{active.Result.Rows.Count} rows returned"
                    : active.Result.Message;
        StatusText = ActiveTab.StatusText;
        ResultChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DescribeRun(IReadOnlyList<QueryResult> results, bool append)
    {
        if (results.Count == 0)
        {
            return "No result";
        }

        var errors = results.Count(result => result.IsError);
        if (results.Count == 1)
        {
            var result = results[0];
            return result.IsError
                ? "Error"
                : result.Messages.Count > 0
                    ? DescribeMessages(result.Messages.Count)
                    : result.IsTruncated
                        ? $"Showing first {result.RowLimit:N0} rows"
                        : $"{result.Rows.Count} rows returned";
        }

        // The Messages tab is not a result set; don't count it as one in the summary.
        var data = results.Where(result => result.Messages.Count == 0).ToList();
        var rows = data.Where(result => !result.IsError).Sum(result => result.Rows.Count);
        var suffix = errors > 0 ? $" · {errors} error{(errors == 1 ? string.Empty : "s")}" : string.Empty;
        var sets = $"result set{(data.Count == 1 ? string.Empty : "s")}";
        return append
            ? $"Added {data.Count} {sets}{suffix}"
            : $"{data.Count} {sets} · {rows} rows{suffix}";
    }

    private static string DescribeMessages(int count) =>
        $"{count} message{(count == 1 ? string.Empty : "s")}";

    /// <summary>Republishes the active tab's result tabs; an empty tab clears the grid.</summary>
    private void SyncResultMirror(TabViewModel? tab)
    {
        SyncResultsMirror(tab);
        ResultChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncResultsMirror(TabViewModel? tab)
    {
        var source = tab?.Results;
        if (source is null)
        {
            Results.Clear();
        }
        else if (!Results.SequenceEqual(source))
        {
            Results.Clear();
            foreach (var result in source)
            {
                Results.Add(result);
            }
        }

        HasMultipleResults = Results.Count > 1;
        var active = tab?.ActiveResult;
        LastResult = active?.Result;
        ResultMessage = active?.Message ?? string.Empty;
        ResultSummary = active?.Summary ?? string.Empty;
    }

    public void SelectSpace(SpaceViewModel space, TabViewModel? tab = null)
    {
        foreach (var candidate in Spaces)
        {
            candidate.IsActive = candidate == space;
        }

        ActiveSpace = space;

        // An undo belongs to the workspace where the grouping happened.
        _lastGrouping = [];
        ClearGroupUndo();
        RefreshTabRail();

        var target = tab ?? space.LastActiveTab ?? space.Tabs.FirstOrDefault();
        SelectTab(target);

        if (target is null)
        {
            ApplyDatabase(space.LastActiveDatabase ?? space.ActiveDatabase);
        }

        OnPropertyChanged(nameof(Snippets));
        OnPropertyChanged(nameof(ActiveLanguage));
        OnPropertyChanged(nameof(ActiveSpace));
        NotifyWorkspaceList();
    }

    /// <summary>
    /// Moves <paramref name="offset"/> workspaces along the visible rail order (negative = left).
    /// Returns false at the ends, so a swipe never wraps around; hidden workspaces are skipped.
    /// </summary>
    public bool SelectRelativeSpace(int offset)
    {
        if (ActiveSpace is null)
        {
            return false;
        }

        var rail = RailSpaces;
        var index = rail.ToList().IndexOf(ActiveSpace);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= rail.Count)
        {
            return false;
        }

        SelectSpace(rail[target]);
        return true;
    }

    /// <summary>The footer is derived from the space list; the list itself only changes structurally.</summary>
    private void NotifyWorkspaceList()
    {
        OnPropertyChanged(nameof(HasSpaces));
        OnPropertyChanged(nameof(WelcomeVisibility));
        OnPropertyChanged(nameof(WorkspaceChromeVisibility));
        OnPropertyChanged(nameof(RailSpaces));
        OnPropertyChanged(nameof(FooterSpaces));
        OnPropertyChanged(nameof(HiddenSpaceCount));
        OnPropertyChanged(nameof(HiddenSpaceCountLabel));
        OnPropertyChanged(nameof(ShowsWorkspacePill));
        OnPropertyChanged(nameof(FooterIconsVisibility));
        OnPropertyChanged(nameof(FooterPillVisibility));
        NotifyWorkspaceFooter();
    }

    /// <summary>Position and chevrons follow the active workspace, without rebuilding the icon strip.</summary>
    private void NotifyWorkspaceFooter()
    {
        OnPropertyChanged(nameof(WorkspacePositionText));
        OnPropertyChanged(nameof(CanGoToPreviousSpace));
        OnPropertyChanged(nameof(CanGoToNextSpace));
    }

    /// <summary>Re-tints the active workspace; the change schedules a save through TrackSpace.</summary>
    public void SetWorkspaceColor(string hex)
    {
        if (ActiveSpace is not null)
        {
            ActiveSpace.ColorHex = hex;
        }
    }

    /// <summary>
    /// Renames a workspace from the switcher. A blank name is ignored so the rail never loses the
    /// label that identifies it; <see cref="TrackSpace"/> persists the change.
    /// </summary>
    public bool RenameSpace(SpaceViewModel space, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, space.Name, StringComparison.Ordinal))
        {
            return false;
        }

        space.Name = trimmed;
        StatusText = $"Renamed workspace to {trimmed}";
        return true;
    }

    /// <summary>
    /// Hides or shows a workspace in the rail and switcher. The workspace keeps its tabs, session
    /// and connection; hiding the open one only takes effect in the rail after the user switches
    /// away, so the current context never disappears underneath them.
    /// </summary>
    public void ToggleWorkspaceHidden(SpaceViewModel space)
    {
        if (!Spaces.Contains(space))
        {
            return;
        }

        space.IsHidden = !space.IsHidden;
        NotifyWorkspaceList();
        ScheduleSave();

        StatusText = space.IsHidden
            ? ReferenceEquals(space, ActiveSpace)
                ? $"{space.Name} hidden · it leaves the rail after you switch away"
                : $"Hidden {space.Name} from the rail"
            : $"Shown {space.Name} in the rail";
    }

    /// <summary>
    /// Switches the active tab's database. The tab, not the space, owns this choice: other tabs
    /// keep their own database and result grid.
    /// </summary>
    public async Task SelectDatabaseAsync(DatabaseViewModel database)
    {
        if (ActiveSpace is null)
        {
            return;
        }

        if (ActiveTab is { HasOpenTransaction: true })
        {
            StatusText = "Commit or roll back the transaction before switching databases.";
            return;
        }

        ApplyDatabase(database);

        if (ActiveTab is not null)
        {
            ActiveTab.Database = database.Name;
        }

        StatusText = $"{database.Name} · {ActiveSpace.StatusText}";

        if (ActiveTab is not null)
        {
            await SyncSchemaForTabAsync(ActiveSpace, ActiveTab);
        }
    }

    /// <summary>
    /// Mirrors a <c>USE</c> into the shell. The provider reports the session's real database after a
    /// run; if it differs from the tab's, the tab, rail, picker and completion schema adopt it, so
    /// the next run is not switched back to the old database.
    /// </summary>
    private async Task AdoptCurrentDatabaseAsync(TabViewModel tab, SpaceViewModel space)
    {
        var current = await tab.Session.GetCurrentDatabaseAsync(_lifetime.Token);
        if (string.IsNullOrWhiteSpace(current)
            || string.Equals(current, tab.Database, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        tab.Database = current;

        if (!ReferenceEquals(space, ActiveSpace) || !ReferenceEquals(tab, ActiveTab))
        {
            ScheduleSave();
            return;
        }

        var database = space.FindDatabase(current);
        if (database is not null)
        {
            ApplyDatabase(database);
        }

        await SyncSchemaForTabAsync(space, tab);
        StatusText = database is not null
            ? $"{database.Name} · {space.StatusText}"
            : $"{current} · {space.StatusText}";
    }

    public void SelectTab(TabViewModel? tab)
    {
        if (tab is null)
        {
            ActiveTab = null;
            SyncResultMirror(null);
            StatusText = "Ready";
            return;
        }

        // Selecting a tab must never leave it hidden inside a collapsed group.
        if (tab.Group is { IsExpanded: false } collapsed)
        {
            collapsed.IsExpanded = true;
            RefreshTabRail();
        }

        foreach (var candidate in Tabs)
        {
            candidate.IsActive = candidate == tab;
        }

        ActiveTab = tab;
        tab.LastAccessedAt = DateTimeOffset.UtcNow;
        IsArchiveOpen = false;

        if (ActiveSpace is not null && ReferenceEquals(tab.Space, ActiveSpace))
        {
            ActiveSpace.LastActiveTab = tab;
            ApplyTabDatabase(tab);
        }

        SyncResultMirror(tab);

        // A run can finish while another tab is active; this restores that tab's own status
        // instead of leaving the shell stuck on "Running…".
        StatusText = string.IsNullOrWhiteSpace(tab.StatusText) ? "Ready" : tab.StatusText;
        ActiveTabChanged?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    /// <summary>Points the space's database context at the tab's database and reloads completion.</summary>
    private void ApplyTabDatabase(TabViewModel tab)
    {
        if (ActiveSpace is null)
        {
            return;
        }

        var database = ActiveSpace.FindDatabase(tab.Database);
        if (database is not null)
        {
            ApplyDatabase(database);
        }
        else
        {
            // Before the connection opens the list is empty: remember the tab's database so the
            // connect flow reselects it, instead of falling back to an unrelated default.
            foreach (var candidate in ActiveSpace.Databases)
            {
                candidate.IsActive = false;
            }

            ActiveSpace.ActiveDatabase = null;
            ActiveSpace.LastActiveDatabase = null;
            if (!string.IsNullOrWhiteSpace(tab.Database))
            {
                ActiveSpace.PreferredDatabase = tab.Database;
            }
        }

        if (ActiveSpace.Connection.IsConnected)
        {
            _ = SyncSchemaForTabAsync(ActiveSpace, tab);
        }
        else
        {
            ClearSchema();
        }
    }

    public async Task CloseTabAsync(TabViewModel tab)
    {
        var space = tab.Space;

        // The tab owns its session: stop a running query and roll back any open transaction
        // before the tab disappears.
        tab.Session.Cancel();
        await tab.CloseSessionAsync(CancellationToken.None);

        // Closing is now non-destructive: the tab moves to the archive page and can be restored.
        if (!ArchiveTab(tab, notify: false, allowLast: true))
        {
            return;
        }

        if (space.Tabs.Count == 0)
        {
            var replacement = new TabViewModel(
                Guid.NewGuid().ToString("n"),
                $"Query {++_queryCounter}",
                space.ActiveDatabase?.Name ?? string.Empty,
                space,
                string.Empty);

            space.Tabs.Insert(0, replacement);
            if (ReferenceEquals(space, ActiveSpace))
            {
                RefreshTabRail();
                SelectTab(replacement);
            }
        }

        StatusText = $"Closed {tab.Title} · moved to archive";
    }

    /// <summary>
    /// Rebuilds the two rail bands from the active space. A group's header is followed by its
    /// tabs only while the group is expanded; collapsed groups contribute one row.
    /// </summary>
    private void RefreshTabRail()
    {
        Tabs.Clear();
        PinnedRail.Clear();
        UnpinnedRail.Clear();

        if (ActiveSpace is { } space)
        {
            foreach (var tab in space.Tabs)
            {
                Tabs.Add(tab);
            }

            foreach (var group in space.Groups.Where(candidate => candidate.IsPinned))
            {
                PinnedRail.Add(group);
                if (group.IsExpanded)
                {
                    foreach (var tab in group.Tabs)
                    {
                        PinnedRail.Add(tab);
                    }
                }
            }

            foreach (var tab in space.Tabs.Where(tab => tab.IsPinned && tab.Group is null))
            {
                PinnedRail.Add(tab);
            }

            foreach (var group in space.Groups.Where(candidate => !candidate.IsPinned))
            {
                UnpinnedRail.Add(group);
                if (group.IsExpanded)
                {
                    foreach (var tab in group.Tabs)
                    {
                        UnpinnedRail.Add(tab);
                    }
                }
            }

            foreach (var tab in space.Tabs.Where(tab => !tab.IsPinned && tab.Group is null))
            {
                UnpinnedRail.Add(tab);
            }

            CanGroupByDatabase = HasUngroupedDatabaseCluster(space);
        }
        else
        {
            CanGroupByDatabase = false;
        }

        HasPinnedTabs = PinnedRail.Count > 0;
    }

    /// <summary>
    /// Persisted order is the visual order: pinned groups, loose pinned tabs, unpinned groups,
    /// loose unpinned tabs. Every band is represented, so collapsing a group never loses tabs.
    /// </summary>
    private static void RebuildTabOrder(
        SpaceViewModel space,
        IReadOnlyList<TabViewModel>? pinnedLooseOrder = null,
        IReadOnlyList<TabViewModel>? unpinnedLooseOrder = null)
    {
        PruneEmptyGroups(space);

        var ordered = new List<TabViewModel>();
        var seen = new HashSet<TabViewModel>();

        void Take(TabViewModel tab)
        {
            if (seen.Add(tab))
            {
                ordered.Add(tab);
            }
        }

        foreach (var group in space.Groups.Where(candidate => candidate.IsPinned))
        {
            foreach (var tab in group.Tabs)
            {
                Take(tab);
            }
        }

        // A drag reorders only the rows it can see; collapsed members keep their relative order.
        foreach (var tab in pinnedLooseOrder
            ?? space.Tabs.Where(tab => tab.IsPinned && tab.Group is null).ToList())
        {
            Take(tab);
        }

        foreach (var group in space.Groups.Where(candidate => !candidate.IsPinned))
        {
            foreach (var tab in group.Tabs)
            {
                Take(tab);
            }
        }

        foreach (var tab in unpinnedLooseOrder
            ?? space.Tabs.Where(tab => !tab.IsPinned && tab.Group is null).ToList())
        {
            Take(tab);
        }

        // A tab whose group vanished is loose, not invisible.
        foreach (var tab in space.Tabs)
        {
            if (seen.Add(tab))
            {
                tab.Group = null;
                ordered.Add(tab);
            }
        }

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = space.Tabs.IndexOf(ordered[target]);
            if (current >= 0 && current != target)
            {
                space.Tabs.Move(current, target);
            }
        }
    }

    /// <summary>Index where a loose tab belongs: top of the loose section, before older loose tabs.</summary>
    private static int LooseInsertIndex(SpaceViewModel space)
    {
        var firstLoose = space.Tabs.FirstOrDefault(tab => !tab.IsPinned && tab.Group is null);
        return firstLoose is null ? space.Tabs.Count : space.Tabs.IndexOf(firstLoose);
    }

    /// <summary>True when grouping would create at least one group.</summary>
    private static bool HasUngroupedDatabaseCluster(SpaceViewModel space)
    {
        var grouped = space.Groups
            .Select(group => group.Database)
            .Where(database => database is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return space.Tabs
            .Where(tab => !tab.IsPinned
                && tab.Group is null
                && !string.IsNullOrWhiteSpace(tab.Database)
                && !grouped.Contains(tab.Database))
            .GroupBy(tab => tab.Database, StringComparer.OrdinalIgnoreCase)
            .Any(bucket => bucket.Count() >= 2);
    }

    /// <summary>Status-bar caret readout, reported by the editor surface after every move.</summary>
    public void SetCaret(int line, int column) => CaretText = $"Ln {line}, Col {column}";

    /// <summary>
    /// A rail ListView reordered its band. Rebuild membership from where each row landed: a tab
    /// under a header joins that group, and a tab without one becomes loose in that band.
    /// </summary>
    public void SyncRailOrder(bool pinnedBand)
    {
        if (ActiveSpace is not { } space)
        {
            return;
        }

        ClearGroupUndo();
        _lastGrouping = [];
        var rail = pinnedBand ? PinnedRail : UnpinnedRail;
        TabGroupViewModel? current = null;
        var loose = new List<TabViewModel>();
        var groupOrder = new Dictionary<TabGroupViewModel, List<TabViewModel>>();

        foreach (var item in rail)
        {
            switch (item)
            {
                case TabGroupViewModel group:
                    current = group;
                    groupOrder[group] = [];
                    break;

                case TabViewModel tab:
                    if (current is null)
                    {
                        loose.Add(tab);
                    }
                    else
                    {
                        groupOrder[current].Add(tab);
                    }

                    break;
            }
        }

        foreach (var (group, order) in groupOrder)
        {
            // Dropping a tab on a collapsed header opens it, so the move is visible.
            if (order.Count > 0)
            {
                group.IsExpanded = true;
            }

            foreach (var tab in order)
            {
                if (!ReferenceEquals(tab.Group, group))
                {
                    tab.Group?.Tabs.Remove(tab);
                    if (!group.Tabs.Contains(tab))
                    {
                        group.Tabs.Add(tab);
                    }

                    tab.Group = group;
                }

                tab.IsPinned = false;
                tab.NotifyGroupMembershipChanged();
            }

            ReorderGroup(group, order);
        }

        foreach (var tab in loose)
        {
            tab.Group?.Tabs.Remove(tab);
            tab.Group = null;
            tab.IsPinned = pinnedBand;
            tab.NotifyGroupMembershipChanged();
        }

        PruneEmptyGroups(space);
        RebuildTabOrder(
            space,
            pinnedLooseOrder: pinnedBand ? loose : null,
            unpinnedLooseOrder: pinnedBand ? null : loose);
        RefreshTabRail();
        ScheduleSave();
    }

    private static void ReorderGroup(TabGroupViewModel group, IReadOnlyList<TabViewModel> order)
    {
        var target = 0;
        foreach (var tab in order)
        {
            var current = group.Tabs.IndexOf(tab);
            if (current < 0)
            {
                continue;
            }

            if (current != target)
            {
                group.Tabs.Move(current, target);
            }

            target++;
        }
    }

    /// <summary>The results ListView reordered the mirrored collection; reorder the tab's own set.</summary>
    public void SyncResultOrder() => ActiveTab?.ApplyResultOrder(Results);

    // ---------------------------------------------------------------- persistence

    /// <summary>Writes the session immediately; used when the window closes.</summary>
    public void SaveNow()
    {
        _saveTimer?.Stop();
        _store.Save(CaptureState());
    }

    /// <summary>Debounces writes so typing in the editor does not hit the disk on every keystroke.</summary>
    private void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                return;
            }

            _saveTimer = queue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveNow();
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private StudioState CaptureState() => new()
    {
        ActiveSpaceId = ActiveSpace?.Id,
        QueryCounter = _queryCounter,
        AutoArchiveEnabled = AutoArchiveEnabled,
        AutoArchiveDays = EffectiveAutoArchiveDays,
        WindowBounds = WindowBounds,
        ResultsPaneHeight = ResultsPaneHeight,
        CellPreviewWidth = CellPreviewWidth,
        Theme = ThemeService.ToStorage(Theme),
        Spaces = Spaces.Select(space => new SpaceState
        {
            Id = space.Id,
            Name = space.Name,
            Color = space.ColorHex,
            Icon = space.IconName,
            Hidden = space.IsHidden,
            ProviderId = space.Connection.ProviderId,
            Profile = VisibleProfile(space.Connection),
            ActiveDatabase = space.PreferredDatabase,
            ActiveTabId = space.LastActiveTab?.Id,
            Groups = space.Groups.Select(group => new TabGroupState
            {
                Id = group.Id,
                Name = group.Name,
                Color = group.ColorHex,
                Pinned = group.IsPinned,
                Expanded = group.IsExpanded,
                Database = group.Database,
            }).ToList(),
            Tabs = space.Tabs.Select(tab => new TabState
            {
                Id = tab.Id,
                Title = tab.Title,
                Database = tab.Database,
                Content = tab.Content,
                Pinned = tab.IsPinned,
                LastAccessedAt = tab.LastAccessedAt,
                GroupId = tab.Group?.Id,
            }).ToList(),
        }).ToList(),
        ArchivedTabs = ArchivedTabs.Select(tab => new ArchivedTabState
        {
            Id = tab.Id,
            SpaceId = tab.SpaceId,
            SpaceName = tab.SpaceName,
            Color = tab.ColorHex,
            Title = tab.Title,
            Database = tab.Database,
            Content = tab.Content,
            Pinned = tab.IsPinned,
            ArchivedAt = tab.ArchivedAt,
            GroupName = tab.GroupName,
        }).ToList(),
    };

    /// <summary>The provider's declared secrets never reach the state file.</summary>
    private static Dictionary<string, string> VisibleProfile(ConnectionViewModel connection) =>
        connection.Profile.Values
            .Where(pair => !connection.SecretKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fills provider-declared password fields from Windows Credential Manager.</summary>
    private static void LoadSecrets(SpaceViewModel space)
    {
        foreach (var key in space.Connection.SecretKeys)
        {
            var secret = SecretStore.Read(SecretStore.Target(space.Id, key));
            if (!string.IsNullOrEmpty(secret))
            {
                space.Connection.Profile.Values[key] = secret;
            }
        }
    }

    /// <summary>Writes provider-declared password fields to Windows Credential Manager, never SQLite.</summary>
    private static void PersistSecrets(SpaceViewModel space)
    {
        foreach (var key in space.Connection.SecretKeys)
        {
            var target = SecretStore.Target(space.Id, key);
            var value = space.Connection.Profile.Get(key);

            if (string.IsNullOrEmpty(value))
            {
                SecretStore.Delete(target);
            }
            else
            {
                SecretStore.Save(target, space.Name, value);
            }
        }
    }

    private static void DeleteSecrets(SpaceViewModel space, IDatabaseProvider? provider)
    {
        if (provider is null)
        {
            return;
        }

        foreach (var setting in provider.Settings.Where(setting => setting.Kind == ProviderSettingKind.Password))
        {
            SecretStore.Delete(SecretStore.Target(space.Id, setting.Key));
        }
    }

    private void Restore(StudioState state)
    {
        WindowBounds = state.WindowBounds;
        ResultsPaneHeight = state.ResultsPaneHeight;
        CellPreviewWidth = state.CellPreviewWidth;
        Theme = ThemeService.Parse(state.Theme);
        _queryCounter = Math.Max(0, state.QueryCounter);
        AutoArchiveEnabled = state.AutoArchiveEnabled;
        AutoArchiveDays = Math.Clamp(state.AutoArchiveDays <= 0 ? 30 : state.AutoArchiveDays, 1, 3650);

        foreach (var spaceState in state.Spaces)
        {
            var providerId = string.IsNullOrWhiteSpace(spaceState.ProviderId) ? "mssql" : spaceState.ProviderId;
            var profile = new ConnectionProfile { ProviderId = providerId };
            foreach (var pair in spaceState.Profile)
            {
                profile.Values[pair.Key] = pair.Value;
            }

            var color = string.IsNullOrWhiteSpace(spaceState.Color)
                ? StudioColors.Palette[Spaces.Count % StudioColors.Palette.Length]
                : spaceState.Color;

            var space = new SpaceViewModel(
                string.IsNullOrWhiteSpace(spaceState.Id) ? Guid.NewGuid().ToString("n") : spaceState.Id,
                string.IsNullOrWhiteSpace(spaceState.Name) ? $"Space {Spaces.Count + 1}" : spaceState.Name,
                color,
                new ConnectionViewModel(providerId, profile),
                string.IsNullOrWhiteSpace(spaceState.Icon) ? null : spaceState.Icon)
            {
                PreferredDatabase = spaceState.ActiveDatabase,
                IsHidden = spaceState.Hidden,
            };

            foreach (var tabState in spaceState.Tabs)
            {
                var tab = new TabViewModel(
                    string.IsNullOrWhiteSpace(tabState.Id) ? Guid.NewGuid().ToString("n") : tabState.Id,
                    tabState.Title,
                    tabState.Database,
                    space,
                    tabState.Content)
                {
                    IsPinned = tabState.Pinned,
                    LastAccessedAt = tabState.LastAccessedAt ?? DateTimeOffset.UtcNow,
                };

                space.Tabs.Add(tab);
            }

            // Groups are restored before membership so a tab can find its group by id.
            foreach (var groupState in spaceState.Groups)
            {
                if (string.IsNullOrWhiteSpace(groupState.Name))
                {
                    continue;
                }

                var group = new TabGroupViewModel(
                    string.IsNullOrWhiteSpace(groupState.Id) ? Guid.NewGuid().ToString("n") : groupState.Id,
                    groupState.Name,
                    string.IsNullOrWhiteSpace(groupState.Color)
                        ? StudioColors.Palette[space.Groups.Count % StudioColors.Palette.Length]
                        : groupState.Color,
                    groupState.Pinned,
                    groupState.Database)
                {
                    IsExpanded = groupState.Expanded,
                };

                space.Groups.Add(group);

                foreach (var tabState in spaceState.Tabs.Where(
                    candidate => string.Equals(candidate.GroupId, group.Id, StringComparison.Ordinal)))
                {
                    if (space.Tabs.FirstOrDefault(candidate => candidate.Id == tabState.Id) is not { } tab)
                    {
                        continue;
                    }

                    tab.Group = group;
                    tab.IsPinned = false;
                    group.Tabs.Add(tab);
                }
            }

            if (space.Tabs.Count == 0)
            {
                space.Tabs.Add(new TabViewModel(Guid.NewGuid().ToString("n"), "Query 1", string.Empty, space, string.Empty));
            }

            // Pin state existed before the pinned section; normalise old sessions so pinned
            // tabs occupy the front of the persisted order.
            RebuildTabOrder(space);

            space.LastActiveTab = space.Tabs.FirstOrDefault(tab => tab.Id == spaceState.ActiveTabId) ?? space.Tabs[0];
            LoadSecrets(space);
            TrackSpace(space);
            Spaces.Add(space);
        }

        foreach (var archived in state.ArchivedTabs)
        {
            var color = string.IsNullOrWhiteSpace(archived.Color)
                ? Spaces.FirstOrDefault(space => space.Id == archived.SpaceId)?.ColorHex
                    ?? StudioColors.Palette[ArchivedTabs.Count % StudioColors.Palette.Length]
                : archived.Color;

            ArchivedTabs.Add(new ArchivedTabViewModel(
                archived.Id,
                archived.SpaceId,
                archived.SpaceName,
                color,
                archived.Title,
                archived.Database,
                archived.Content,
                archived.Pinned,
                archived.ArchivedAt,
                archived.GroupName));
        }

        // A hidden workspace is intentionally out of the rail, so a restart does not reopen it
        // unless nothing visible remains. Falling back keeps the app usable with an all-hidden list.
        var active = Spaces.FirstOrDefault(space => space.Id == state.ActiveSpaceId && !space.IsHidden)
            ?? Spaces.FirstOrDefault(space => !space.IsHidden)
            ?? Spaces.FirstOrDefault(space => space.Id == state.ActiveSpaceId)
            ?? Spaces[0];
        SelectSpace(active, active.LastActiveTab);
    }

    private void TrackSpace(SpaceViewModel space)
    {
        space.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SpaceViewModel.Name) or nameof(SpaceViewModel.ActiveDatabase) or nameof(SpaceViewModel.ColorHex) or nameof(SpaceViewModel.IconName) or nameof(SpaceViewModel.IsHidden))
            {
                ScheduleSave();

                // Hiding changes the rail and switcher membership, not just one row's look.
                if (args.PropertyName == nameof(SpaceViewModel.IsHidden))
                {
                    NotifyWorkspaceList();
                }
            }
        };

        space.Tabs.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
            {
                foreach (TabViewModel tab in args.NewItems)
                {
                    TrackTab(tab);
                }
            }

            ScheduleSave();
        };

        foreach (var tab in space.Tabs)
        {
            TrackTab(tab);
        }
    }

    private void TrackTab(TabViewModel tab)
    {
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(TabViewModel.Content) or nameof(TabViewModel.Title) or nameof(TabViewModel.Database))
            {
                if (args.PropertyName == nameof(TabViewModel.Content))
                {
                    tab.LastAccessedAt = DateTimeOffset.UtcNow;
                }

                ScheduleSave();
            }

            if (args.PropertyName == nameof(TabViewModel.IsPinned))
            {
                ScheduleSave();
            }
        };
    }

    public async Task ConnectSpaceAsync(SpaceViewModel space)
    {
        IsBusy = true;
        StatusText = $"Connecting to {space.Name}…";

        try
        {
            LoadSecrets(space);
            await space.Connection.ConnectAsync(_lifetime.Token);
            var databases = await space.Connection.GetDatabaseDetailsAsync(_lifetime.Token);
            space.UpdateDatabases(databases);

            // The connection is new, so any cached schema may be stale.
            _schemaCache.Clear();

            if (ReferenceEquals(space, ActiveSpace))
            {
                // The "connect first" hint is no longer true once the connection is open.
                if (ResultMessage.StartsWith("Connect the workspace", StringComparison.Ordinal))
                {
                    ResultMessage = string.Empty;
                }

                if (ActiveTab is { } tab)
                {
                    // Keep the tab's database when the server still exposes it; otherwise adopt
                    // the connection default. Tabs created while disconnected gain one here.
                    var target = space.FindDatabase(tab.Database)
                        ?? space.ActiveDatabase
                        ?? space.Databases.FirstOrDefault();

                    if (target is not null)
                    {
                        if (!string.Equals(tab.Database, target.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            tab.Database = target.Name;
                        }

                        ApplyDatabase(target);
                        foreach (var candidate in space.Tabs.Where(t => string.IsNullOrWhiteSpace(t.Database)))
                        {
                            candidate.Database = target.Name;
                        }
                    }

                    await SyncSchemaForTabAsync(space, tab);
                }
                else
                {
                    ApplyDatabase(space.ActiveDatabase);
                }
            }

            var databaseLabel = databases.Count == 1 ? "1 database" : $"{databases.Count} databases";
            StatusText = $"Connected · {space.Connection.Endpoint} · {databaseLabel}";
        }
        catch (Exception ex)
        {
            StatusText = "Connection failed";
            if (ActiveTab is { } tab && ReferenceEquals(space, ActiveSpace))
            {
                ApplyResults(tab, [QueryResult.Error(ex.Message, TimeSpan.Zero)], append: false, "Connection failed");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectSpaceAsync(SpaceViewModel space)
    {
        IsBusy = true;
        try
        {
            // Each tab owns a session, so disconnecting the workspace must close them all.
            foreach (var tab in space.Tabs)
            {
                await tab.CloseSessionAsync(CancellationToken.None);
                tab.StatusText = "Disconnected";
            }

            await space.Connection.DisconnectAsync(_lifetime.Token);
            space.UpdateDatabases([]);
            _schemaCache.Clear();
            ClearSchema();
            StatusText = $"Disconnected from {space.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"Disconnect failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads the schema for the tab's database, from cache when possible. Completion is scoped to
    /// the active tab, so an in-flight load for a tab the user already left is discarded.
    /// </summary>
    private async Task SyncSchemaForTabAsync(SpaceViewModel space, TabViewModel tab)
    {
        if (!ReferenceEquals(space, ActiveSpace) || !ReferenceEquals(tab, ActiveTab))
        {
            return;
        }

        var database = DatabaseNameFor(space, tab);
        if (!space.Connection.IsConnected || string.IsNullOrWhiteSpace(database))
        {
            ClearSchema();
            return;
        }

        var key = SchemaKey(space, database);
        if (_schemaCache.TryGetValue(key, out var cached))
        {
            ApplySchema(cached);
            return;
        }

        ClearSchema();

        try
        {
            var schema = await tab.Session.GetSchemaAsync(database, _lifetime.Token);

            // Routine metadata is an enhancement: a provider that cannot read it must not cost
            // the user table/browser completion for the database.
            IReadOnlyList<RoutineSchema> routines;
            try
            {
                routines = await tab.Session.GetRoutinesAsync(database, _lifetime.Token);
            }
            catch
            {
                routines = [];
            }

            var snapshot = new SchemaSnapshot(schema, routines);
            _schemaCache[key] = snapshot;

            if (ReferenceEquals(space, ActiveSpace)
                && ReferenceEquals(tab, ActiveTab)
                && string.Equals(DatabaseNameFor(space, tab), database, StringComparison.OrdinalIgnoreCase))
            {
                ApplySchema(snapshot);
            }
        }
        catch
        {
            // Completion degrades to keywords/snippets; the session itself stays usable.
        }
    }

    /// <summary>The database a tab is bound to, falling back to the space default only for an unbound tab.</summary>
    private static string? DatabaseNameFor(SpaceViewModel space, TabViewModel tab) =>
        string.IsNullOrWhiteSpace(tab.Database)
            ? space.ActiveDatabase?.Name
            : space.FindDatabase(tab.Database)?.Name ?? tab.Database;

    private static string SchemaKey(SpaceViewModel space, string database) =>
        $"{space.Id}\u001F{database}";

    private void ClearSchema()
    {
        _schema = [];
        _routines = [];
        OnPropertyChanged(nameof(Tables));
        OnPropertyChanged(nameof(Routines));
    }

    private void ApplySchema(SchemaSnapshot snapshot)
    {
        _schema = snapshot.Tables;
        _routines = snapshot.Routines;
        OnPropertyChanged(nameof(Tables));
        OnPropertyChanged(nameof(Routines));
    }

    /// <summary>What one database contributes to completion and browsing, cached together.</summary>
    private sealed record SchemaSnapshot(
        IReadOnlyList<TableSchema> Tables,
        IReadOnlyList<RoutineSchema> Routines);

    /// <summary>Sets the space's active database without touching the active tab.</summary>
    private void ApplyDatabase(DatabaseViewModel? database)
    {
        if (ActiveSpace is null)
        {
            return;
        }

        foreach (var candidate in ActiveSpace.Databases)
        {
            candidate.IsActive = candidate == database;
        }

        ActiveSpace.ActiveDatabase = database;
        ActiveSpace.LastActiveDatabase = database;
        if (database is not null)
        {
            ActiveSpace.PreferredDatabase = database.Name;
        }

        // Snippets are scoped per dialect, which the connection decides.
        OnPropertyChanged(nameof(Snippets));
        OnPropertyChanged(nameof(ActiveLanguage));
    }
}
