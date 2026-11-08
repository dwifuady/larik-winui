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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _archiveTimer;
    private int _queryCounter;
    private IReadOnlyList<TableSchema> _schema = [];

    // Schemas are per database; keeping them makes switching tabs back to a database instant.
    private readonly Dictionary<string, IReadOnlyList<TableSchema>> _schemaCache = new(StringComparer.OrdinalIgnoreCase);

    public StudioViewModel()
    {
        StatusText = "Ready";
        CaretText = "Ln 1, Col 1";
        ResultMessage = string.Empty;
        ResultSummary = string.Empty;
        IsSidebarOpen = true;

        if (_store.Load() is { Spaces.Count: > 0 } saved)
        {
            Restore(saved);
        }
        else
        {
            Seed();
            SelectSpace(Spaces[0]);
        }

        ResultMessage = "Connect the workspace, then run a query with Ctrl+Enter";

        RefreshArchiveView();

        Spaces.CollectionChanged += (_, _) => ScheduleSave();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ActiveSpace) or nameof(ActiveTab))
            {
                ScheduleSave();
            }
        };
    }

    public ObservableCollection<SpaceViewModel> Spaces { get; } = [];

    /// <summary>Tabs of the active space. Mutated in place so the ListView keeps its identity.</summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>Pinned tabs of the active space, rendered above the TABS header.</summary>
    public ObservableCollection<TabViewModel> PinnedTabs { get; } = [];

    /// <summary>Unpinned tabs of the active space, rendered below the new-tab row.</summary>
    public ObservableCollection<TabViewModel> UnpinnedTabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinnedTabsVisibility))]
    public partial bool HasPinnedTabs { get; set; }

    public Visibility PinnedTabsVisibility => HasPinnedTabs ? Visibility.Visible : Visibility.Collapsed;

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
    public partial bool HasMultipleResults { get; set; }

    public Visibility ResultTabsVisibility => HasMultipleResults ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarVisibility))]
    public partial bool IsSidebarOpen { get; set; }

    [ObservableProperty]
    public partial SpaceViewModel? ActiveSpace { get; set; }

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

    public string WindowTitle => ActiveTab is null ? "Larik SQL Studio" : $"{ActiveTab.Title} — Larik";

    public IReadOnlyList<SnippetDef> Snippets =>
        SnippetCatalog.ForDialect(ActiveSpace?.Connection.Dialect ?? "mssql");

    /// <summary>Schema of the active database, used for completion. Empty until a connection opens.</summary>
    public IReadOnlyList<TableSchema> Tables => _schema;

    public QueryResult? LastResult { get; private set; }

    public event EventHandler? ResultChanged;

    public event EventHandler? ActiveTabChanged;

    /// <summary>The shell shows the database picker before creating a tab; the view owns dialogs.</summary>
    public event EventHandler? NewTabRequested;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (ActiveSpace is null || IsBusy)
        {
            return;
        }

        if (ActiveSpace.Connection.IsConnected)
        {
            await DisconnectSpaceAsync(ActiveSpace);
        }
        else
        {
            await ConnectSpaceAsync(ActiveSpace);
        }
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

    public void TogglePin(TabViewModel tab)
    {
        var space = tab.Space;
        var current = space.Tabs.IndexOf(tab);
        if (current < 0)
        {
            return;
        }

        if (!tab.IsPinned)
        {
            tab.IsPinned = true;
            space.Tabs.Move(current, 0);
            StatusText = $"Pinned {tab.Title}";
        }
        else
        {
            tab.IsPinned = false;
            var target = space.Tabs.Count(candidate => candidate.IsPinned);
            space.Tabs.Move(current, Math.Clamp(target, 0, space.Tabs.Count - 1));
            StatusText = $"Unpinned {tab.Title}";
        }

        tab.LastAccessedAt = DateTimeOffset.UtcNow;
        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabSections();
        }

        ScheduleSave();
    }

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

        var wasActive = ReferenceEquals(ActiveTab, tab);
        var entry = new ArchivedTabViewModel(
            tab.Id,
            space.Id,
            space.Name,
            space.ColorHex,
            tab.Title,
            tab.Database,
            tab.Content,
            tab.IsPinned,
            DateTimeOffset.UtcNow);

        ArchivedTabs.Insert(0, entry);
        if (ReferenceEquals(space.LastActiveTab, tab))
        {
            space.LastActiveTab = null;
        }

        space.Tabs.Remove(tab);

        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabSections();
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

        var index = archived.IsPinned ? 0 : space.Tabs.Count(candidate => candidate.IsPinned);
        space.Tabs.Insert(index, tab);

        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabSections();
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

        var tab = new TabViewModel(
            Guid.NewGuid().ToString("n"),
            $"Query {++_queryCounter}",
            databaseName ?? string.Empty,
            space,
            string.Empty);

        // A new tab opens above the other unpinned tabs, like Arc's new-tab row.
        space.Tabs.Insert(space.Tabs.Count(existing => existing.IsPinned), tab);
        if (ReferenceEquals(space, ActiveSpace))
        {
            RefreshTabSections();
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
    public SpaceViewModel CreateSpace(string name, string colorHex, string providerId, ConnectionProfile profile)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? $"Space {Spaces.Count + 1}" : name.Trim();
        var color = string.IsNullOrWhiteSpace(colorHex)
            ? StudioColors.Palette[Spaces.Count % StudioColors.Palette.Length]
            : colorHex;

        var space = new SpaceViewModel(
            Guid.NewGuid().ToString("n"),
            displayName,
            color,
            new ConnectionViewModel(providerId, profile));

        space.Tabs.Add(new TabViewModel(Guid.NewGuid().ToString("n"), "Query 1", string.Empty, space, string.Empty));

        PersistSecrets(space);
        TrackSpace(space);
        Spaces.Add(space);
        SelectSpace(space);
        ScheduleSave();
        return space;
    }

    /// <summary>Applies provider + profile from the connection dialog, then connects.</summary>
    public async Task ApplyConnectionSettingsAsync(string providerId, ConnectionProfile profile, bool connect)
    {
        if (ActiveSpace is null)
        {
            return;
        }

        var previousProvider = ActiveSpace.Connection.Provider;
        var previousProviderId = ActiveSpace.Connection.ProviderId;

        await ActiveSpace.Connection.ReconfigureAsync(providerId, profile);
        if (!string.Equals(previousProviderId, providerId, StringComparison.OrdinalIgnoreCase))
        {
            DeleteSecrets(ActiveSpace, previousProvider);
        }

        PersistSecrets(ActiveSpace);
        _schemaCache.Clear();
        ClearSchema();
        ActiveSpace.UpdateDatabases([]);
        OnPropertyChanged(nameof(Snippets));
        StatusText = $"{ActiveSpace.Name} → {ActiveSpace.Connection.ProviderName}";
        ScheduleSave();

        if (connect)
        {
            await ConnectSpaceAsync(ActiveSpace);
        }
    }

    /// <summary>
    /// Runs SQL the shell resolved from the editor (a selection, the statement under the caret,
    /// or the whole document) on the active tab. The tab pins the database and the target result,
    /// so switching tabs or spaces while the query runs cannot retarget it.
    /// </summary>
    public async Task RunSqlAsync(string sql, bool append = false)
    {
        var tab = ActiveTab;
        var space = ActiveSpace;
        if (tab is null || space is null || IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusText = "Nothing to run";
            return;
        }

        if (!space.Connection.IsConnected)
        {
            ApplyResults(tab, [QueryResult.Error(
                $"Not connected — connect to {space.Name} to run queries.",
                TimeSpan.Zero)], append, "Disconnected");
            return;
        }

        var upper = sql.ToUpperInvariant();
        var mutates = upper.Contains("UPDATE ") || upper.Contains("DELETE ") || upper.Contains("TRUNCATE ");
        var guarded = upper.Contains("WHERE");

        if (mutates && !guarded)
        {
            ApplyResults(tab, [QueryResult.Error(
                "Refused: this statement modifies rows without a WHERE clause.",
                TimeSpan.Zero)], append, "Error");
            return;
        }

        IsBusy = true;
        StatusText = "Running…";
        try
        {
            // The provider applies the row cap while reading and returns one result per result set.
            var results = await space.Connection.ExecuteBatchAsync(
                sql,
                DatabaseNameFor(space, tab),
                QueryExecutionOptions.Default,
                _lifetime.Token);
            ApplyResults(tab, results, append);
        }
        catch (Exception ex)
        {
            ApplyResults(tab, [QueryResult.Error(ex.Message, TimeSpan.Zero)], append);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Format()
    {
        if (ActiveTab is not null)
        {
            ActiveTab.Content = SqlFormatter.Format(ActiveTab.Content);
            ActiveTabChanged?.Invoke(this, EventArgs.Empty);
            StatusText = "Formatted";
        }
    }

    /// <summary>Stores new result tabs on their query tab and mirrors the active one to the shell.</summary>
    private void ApplyResults(
        TabViewModel tab,
        IReadOnlyList<QueryResult> results,
        bool append,
        string? status = null)
    {
        tab.ApplyResults(results, append);

        if (!ReferenceEquals(tab, ActiveTab))
        {
            return;
        }

        SyncResultsMirror(tab);
        StatusText = status ?? DescribeRun(results, append);
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
        StatusText = result.Result.IsError
            ? "Error"
            : result.Result.Rows.Count > 0
                ? $"{result.Result.Rows.Count} rows returned"
                : result.Result.Message;
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
        StatusText = ActiveTab.ActiveResult is not { } active
            ? "No results"
            : active.Result.IsError
                ? "Error"
                : active.Result.Rows.Count > 0
                    ? $"{active.Result.Rows.Count} rows returned"
                    : active.Result.Message;
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
                : result.IsTruncated
                    ? $"Showing first {result.RowLimit:N0} rows"
                    : $"{result.Rows.Count} rows returned";
        }

        var rows = results.Where(result => !result.IsError).Sum(result => result.Rows.Count);
        var suffix = errors > 0 ? $" · {errors} error{(errors == 1 ? string.Empty : "s")}" : string.Empty;
        var sets = $"result set{(results.Count == 1 ? string.Empty : "s")}";
        return append
            ? $"Added {results.Count} {sets}{suffix}"
            : $"{results.Count} {sets} · {rows} rows{suffix}";
    }

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

        RefreshTabSections();

        var target = tab ?? space.LastActiveTab ?? space.Tabs.FirstOrDefault();
        SelectTab(target);

        if (target is null)
        {
            ApplyDatabase(space.LastActiveDatabase ?? space.ActiveDatabase);
        }

        OnPropertyChanged(nameof(Snippets));
        OnPropertyChanged(nameof(ActiveSpace));
    }

    /// <summary>
    /// Moves <paramref name="offset"/> workspaces along the dot order (negative = left).
    /// Returns false at the ends, so a swipe never wraps around.
    /// </summary>
    public bool SelectRelativeSpace(int offset)
    {
        if (ActiveSpace is null)
        {
            return false;
        }

        var index = Spaces.IndexOf(ActiveSpace);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Spaces.Count)
        {
            return false;
        }

        SelectSpace(Spaces[target]);
        return true;
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
    /// Switches the active tab's database. The tab, not the space, owns this choice: other tabs
    /// keep their own database and result grid.
    /// </summary>
    public async Task SelectDatabaseAsync(DatabaseViewModel database)
    {
        if (ActiveSpace is null)
        {
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

    public void SelectTab(TabViewModel? tab)
    {
        if (tab is null)
        {
            ActiveTab = null;
            SyncResultMirror(null);
            return;
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

    public void CloseTab(TabViewModel tab)
    {
        var space = tab.Space;

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
                RefreshTabSections();
                SelectTab(replacement);
            }
        }

        StatusText = $"Closed {tab.Title} · moved to archive";
    }

    /// <summary>Rebuilds the pinned and unpinned rail sections from the active space.</summary>
    private void RefreshTabSections()
    {
        Tabs.Clear();
        PinnedTabs.Clear();
        UnpinnedTabs.Clear();

        if (ActiveSpace is { } space)
        {
            foreach (var tab in space.Tabs)
            {
                Tabs.Add(tab);
                (tab.IsPinned ? PinnedTabs : UnpinnedTabs).Add(tab);
            }
        }

        HasPinnedTabs = PinnedTabs.Count > 0;
    }

    /// <summary>Moves every pinned tab to the front, preserving their relative order.</summary>
    private static void NormalizePinnedOrder(SpaceViewModel space)
    {
        var target = 0;
        foreach (var tab in space.Tabs.Where(candidate => candidate.IsPinned).ToList())
        {
            var current = space.Tabs.IndexOf(tab);
            if (current != target)
            {
                space.Tabs.Move(current, target);
            }

            target++;
        }
    }

    /// <summary>Status-bar caret readout, reported by the editor surface after every move.</summary>
    public void SetCaret(int line, int column) => CaretText = $"Ln {line}, Col {column}";

    /// <summary>
    /// The rail's two ListViews own the drag gesture and reorder their section. Rebuild the
    /// persisted order as pinned section + unpinned section, without disturbing the lists.
    /// </summary>
    public void SyncTabOrder()
    {
        if (ActiveSpace is not { } space)
        {
            return;
        }

        var ordered = PinnedTabs.Concat(UnpinnedTabs).ToList();
        if (ordered.Count != space.Tabs.Count)
        {
            return;
        }

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = space.Tabs.IndexOf(ordered[target]);
            if (current >= 0 && current != target)
            {
                space.Tabs.Move(current, target);
            }
        }

        // The sections already reflect the drag; only the full mirror needs rebuilding.
        Tabs.Clear();
        foreach (var tab in ordered)
        {
            Tabs.Add(tab);
        }

        ScheduleSave();
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
        Spaces = Spaces.Select(space => new SpaceState
        {
            Id = space.Id,
            Name = space.Name,
            Color = space.ColorHex,
            ProviderId = space.Connection.ProviderId,
            Profile = VisibleProfile(space.Connection),
            ActiveDatabase = space.PreferredDatabase,
            ActiveTabId = space.LastActiveTab?.Id,
            Tabs = space.Tabs.Select(tab => new TabState
            {
                Id = tab.Id,
                Title = tab.Title,
                Database = tab.Database,
                Content = tab.Content,
                Pinned = tab.IsPinned,
                LastAccessedAt = tab.LastAccessedAt,
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
                new ConnectionViewModel(providerId, profile))
            {
                PreferredDatabase = spaceState.ActiveDatabase,
            };

            foreach (var tabState in spaceState.Tabs)
            {
                space.Tabs.Add(new TabViewModel(
                    string.IsNullOrWhiteSpace(tabState.Id) ? Guid.NewGuid().ToString("n") : tabState.Id,
                    tabState.Title,
                    tabState.Database,
                    space,
                    tabState.Content)
                {
                    IsPinned = tabState.Pinned,
                    LastAccessedAt = tabState.LastAccessedAt ?? DateTimeOffset.UtcNow,
                });
            }

            if (space.Tabs.Count == 0)
            {
                space.Tabs.Add(new TabViewModel(Guid.NewGuid().ToString("n"), "Query 1", string.Empty, space, string.Empty));
            }

            // Pin state existed before the pinned section; normalise old sessions so pinned
            // tabs occupy the front of the persisted order.
            NormalizePinnedOrder(space);

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
                archived.ArchivedAt));
        }

        var active = Spaces.FirstOrDefault(space => space.Id == state.ActiveSpaceId) ?? Spaces[0];
        SelectSpace(active, active.LastActiveTab);
    }

    private void TrackSpace(SpaceViewModel space)
    {
        space.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SpaceViewModel.Name) or nameof(SpaceViewModel.ActiveDatabase) or nameof(SpaceViewModel.ColorHex))
            {
                ScheduleSave();
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
            var databases = await space.Connection.GetDatabasesAsync(_lifetime.Token);
            space.UpdateDatabases(databases);

            // The connection is new, so any cached schema may be stale.
            _schemaCache.Clear();

            if (ReferenceEquals(space, ActiveSpace))
            {
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

            StatusText = $"Connected · {space.Connection.Endpoint} · {databases.Count} databases";
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
            _schema = cached;
            OnPropertyChanged(nameof(Tables));
            return;
        }

        ClearSchema();

        try
        {
            var schema = await space.Connection.GetSchemaAsync(database, _lifetime.Token);
            _schemaCache[key] = schema;

            if (ReferenceEquals(space, ActiveSpace)
                && ReferenceEquals(tab, ActiveTab)
                && string.Equals(DatabaseNameFor(space, tab), database, StringComparison.OrdinalIgnoreCase))
            {
                _schema = schema;
                OnPropertyChanged(nameof(Tables));
            }
        }
        catch
        {
            // Completion degrades to keywords/snippets; the session itself stays usable.
        }
    }

    /// <summary>The database a tab is bound to, falling back to the space default when it is unknown.</summary>
    private static string? DatabaseNameFor(SpaceViewModel space, TabViewModel tab) =>
        space.FindDatabase(tab.Database)?.Name ?? space.ActiveDatabase?.Name;

    private static string SchemaKey(SpaceViewModel space, string database) =>
        $"{space.Id}\u001F{database}";

    private void ClearSchema()
    {
        _schema = [];
        OnPropertyChanged(nameof(Tables));
    }

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
    }

    private void Seed()
    {
        var staging = new SpaceViewModel(
            "space-staging",
            "Staging",
            StudioColors.Palette[2],
            new ConnectionViewModel("demo", DemoProfile("larik_staging,larik_sandbox")));
        staging.Tabs.Add(new TabViewModel("t1", "Customer totals", "larik_staging", staging,
            "SELECT c.full_name, COUNT(*) AS orders_count\nFROM customers c\nINNER JOIN orders o ON o.customer_id = c.id\nGROUP BY c.full_name\nORDER BY orders_count DESC;"));

        var localSql = new SpaceViewModel(
            "space-local-sql",
            "Local SQL Server",
            StudioColors.Palette[0],
            new ConnectionViewModel("mssql", SqlServerProfile()));
        localSql.Tabs.Add(new TabViewModel("t2", "Databases", "master", localSql,
            "SELECT name, state_desc\nFROM sys.databases\nORDER BY name;"));

        var playground = new SpaceViewModel(
            "space-playground",
            "Local playground",
            StudioColors.Palette[3],
            new ConnectionViewModel("demo", DemoProfile("playground")));
        playground.Tabs.Add(new TabViewModel("t3", "Notes", "playground", playground,
            "-- Local playground\n-- Connect the workspace, then run this\nSELECT * FROM products;"));

        Spaces.Add(staging);
        Spaces.Add(localSql);
        Spaces.Add(playground);

        foreach (var space in Spaces)
        {
            TrackSpace(space);
        }
    }

    private static ConnectionProfile DemoProfile(string databases)
    {
        var profile = new ConnectionProfile { ProviderId = "demo" };
        profile.Values["databases"] = databases;
        return profile;
    }

    private static ConnectionProfile SqlServerProfile()
    {
        var profile = new ConnectionProfile { ProviderId = "mssql" };
        profile.Values["server"] = @"(localdb)\MSSQLLocalDB";
        profile.Values["encrypt"] = "false";
        profile.Values["trustServerCertificate"] = "true";
        profile.Values["connectTimeout"] = "8";
        return profile;
    }
}
