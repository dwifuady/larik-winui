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

    /// <summary>Result tabs of the active query tab.</summary>
    public ObservableCollection<ResultSetViewModel> Results { get; } = [];

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

        space.Tabs.Add(tab);
        if (ReferenceEquals(space, ActiveSpace))
        {
            Tabs.Add(tab);
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

        await ActiveSpace.Connection.ReconfigureAsync(providerId, profile);
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

    [RelayCommand]
    private Task RunAsync() => RunCoreAsync(append: false);

    [RelayCommand]
    private Task RunNewResultAsync() => RunCoreAsync(append: true);

    private async Task RunCoreAsync(bool append)
    {
        var tab = ActiveTab;
        var space = ActiveSpace;
        if (tab is null || space is null || IsBusy)
        {
            return;
        }

        if (!space.Connection.IsConnected)
        {
            ApplyResults(tab, [QueryResult.Error(
                $"Not connected — connect to {space.Name} to run queries.",
                TimeSpan.Zero)], append, "Disconnected");
            return;
        }

        var sql = tab.Content;
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
            // The tab pins the database, so switching tabs while the query runs cannot retarget it.
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

        Tabs.Clear();
        foreach (var item in space.Tabs)
        {
            Tabs.Add(item);
        }

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

        if (ActiveSpace is not null && ReferenceEquals(tab.Space, ActiveSpace))
        {
            ActiveSpace.LastActiveTab = tab;
            ApplyTabDatabase(tab);
        }

        SyncResultMirror(tab);
        ActiveTabChanged?.Invoke(this, EventArgs.Empty);
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
        var index = space.Tabs.IndexOf(tab);

        space.Tabs.Remove(tab);
        Tabs.Remove(tab);

        if (space.Tabs.Count == 0)
        {
            var replacement = new TabViewModel(
                Guid.NewGuid().ToString("n"),
                $"Query {++_queryCounter}",
                space.ActiveDatabase?.Name ?? string.Empty,
                space,
                string.Empty);

            space.Tabs.Add(replacement);
            Tabs.Add(replacement);
            SelectTab(replacement);
            return;
        }

        if (ActiveTab != tab)
        {
            return;
        }

        SelectTab(space.Tabs[Math.Clamp(index, 0, space.Tabs.Count - 1)]);
    }

    /// <summary>Status-bar caret readout, reported by the editor surface after every move.</summary>
    public void SetCaret(int line, int column) => CaretText = $"Ln {line}, Col {column}";

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
            }).ToList(),
        }).ToList(),
    };

    /// <summary>The provider's declared secrets never reach the state file.</summary>
    private static Dictionary<string, string> VisibleProfile(ConnectionViewModel connection)
    {
        var secrets = connection.Provider?.Settings
            .Where(setting => setting.Kind == ProviderSettingKind.Password)
            .Select(setting => setting.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        return connection.Profile.Values
            .Where(pair => !secrets.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private void Restore(StudioState state)
    {
        _queryCounter = Math.Max(0, state.QueryCounter);

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
                    tabState.Content));
            }

            if (space.Tabs.Count == 0)
            {
                space.Tabs.Add(new TabViewModel(Guid.NewGuid().ToString("n"), "Query 1", string.Empty, space, string.Empty));
            }

            space.LastActiveTab = space.Tabs.FirstOrDefault(tab => tab.Id == spaceState.ActiveTabId) ?? space.Tabs[0];
            TrackSpace(space);
            Spaces.Add(space);
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
