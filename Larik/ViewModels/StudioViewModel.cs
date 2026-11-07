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

    [RelayCommand]
    private void NewTab()
    {
        if (ActiveSpace is null)
        {
            return;
        }

        var tab = new TabViewModel(
            Guid.NewGuid().ToString("n"),
            $"Query {++_queryCounter}",
            ActiveSpace.ActiveDatabase?.Name ?? string.Empty,
            ActiveSpace,
            string.Empty);

        ActiveSpace.Tabs.Add(tab);
        Tabs.Add(tab);
        SelectTab(tab);
        StatusText = $"New query in {ActiveSpace.Name}";
    }

    [RelayCommand]
    private void NewSpace()
    {
        var index = Spaces.Count;
        var color = StudioColors.Palette[index % StudioColors.Palette.Length];
        var provider = DatabaseProviderCatalog.Default;
        var profile = new ConnectionProfile { ProviderId = provider?.Id ?? "mssql" };

        foreach (var setting in provider?.Settings ?? [])
        {
            if (setting.DefaultValue is { } value)
            {
                profile.Values[setting.Key] = value;
            }
        }

        var space = new SpaceViewModel(
            Guid.NewGuid().ToString("n"),
            $"Space {index + 1}",
            color,
            new ConnectionViewModel(profile.ProviderId, profile));

        var tab = new TabViewModel(Guid.NewGuid().ToString("n"), "Query 1", string.Empty, space, string.Empty);
        space.Tabs.Add(tab);
        Spaces.Add(space);
        TrackSpace(space);
        SelectSpace(space);
        StatusText = "New workspace — open Connection settings to point it at a server";
    }

    /// <summary>Applies provider + profile from the connection dialog, then connects.</summary>
    public async Task ApplyConnectionSettingsAsync(string providerId, ConnectionProfile profile, bool connect)
    {
        if (ActiveSpace is null)
        {
            return;
        }

        await ActiveSpace.Connection.ReconfigureAsync(providerId, profile);
        _schema = [];
        OnPropertyChanged(nameof(Tables));
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
    private async Task RunAsync()
    {
        if (ActiveTab is null || ActiveSpace is null || IsBusy)
        {
            return;
        }

        if (!ActiveSpace.Connection.IsConnected)
        {
            LastResult = QueryResult.Error(
                $"Not connected — connect to {ActiveSpace.Name} to run queries.",
                TimeSpan.Zero);
            FinishRun("Disconnected");
            return;
        }

        var sql = ActiveTab.Content;
        var upper = sql.ToUpperInvariant();
        var mutates = upper.Contains("UPDATE ") || upper.Contains("DELETE ") || upper.Contains("TRUNCATE ");
        var guarded = upper.Contains("WHERE");

        if (mutates && !guarded)
        {
            LastResult = QueryResult.Error(
                "Refused: this statement modifies rows without a WHERE clause.",
                TimeSpan.Zero);
            FinishRun("Error");
            return;
        }

        IsBusy = true;
        StatusText = "Running…";
        try
        {
            LastResult = await ActiveSpace.Connection.ExecuteAsync(
                sql,
                ActiveSpace.ActiveDatabase?.Name,
                _lifetime.Token);
        }
        catch (Exception ex)
        {
            LastResult = QueryResult.Error(ex.Message, TimeSpan.Zero);
        }
        finally
        {
            IsBusy = false;
        }

        FinishRun();
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

    private void FinishRun(string? status = null)
    {
        if (LastResult is null)
        {
            return;
        }

        ResultMessage = LastResult.Message;
        ResultSummary = LastResult is { IsError: false, Rows.Count: > 0 }
            ? $"{LastResult.Rows.Count} rows · {LastResult.Elapsed.TotalMilliseconds:0} ms"
            : string.Empty;

        StatusText = status ?? (LastResult.IsError
            ? "Error"
            : $"{LastResult.Rows.Count} rows returned");
        ResultChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectSpace(SpaceViewModel space, TabViewModel? tab = null)
    {
        foreach (var candidate in Spaces)
        {
            candidate.IsActive = candidate == space;
        }

        ActiveSpace = space;
        ApplyDatabase(space.LastActiveDatabase ?? space.ActiveDatabase ?? space.Databases.FirstOrDefault());

        Tabs.Clear();
        foreach (var item in space.Tabs)
        {
            Tabs.Add(item);
        }

        SelectTab(tab ?? space.LastActiveTab ?? space.Tabs.FirstOrDefault());
        OnPropertyChanged(nameof(Snippets));
        OnPropertyChanged(nameof(ActiveSpace));

        if (space.Connection.IsConnected)
        {
            _ = RefreshSchemaAsync(space);
        }
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

    /// <summary>Switches the database the active space queries against and reloads its schema.</summary>
    public async Task SelectDatabaseAsync(DatabaseViewModel database)
    {
        ApplyDatabase(database);

        if (ActiveTab is not null)
        {
            ActiveTab.Database = database.Name;
        }

        StatusText = $"{database.Name} · {ActiveSpace?.StatusText}";

        if (ActiveSpace is not null)
        {
            await RefreshSchemaAsync(ActiveSpace);
        }
    }

    public void SelectTab(TabViewModel? tab)
    {
        if (tab is null)
        {
            ActiveTab = null;
            return;
        }

        foreach (var candidate in Tabs)
        {
            candidate.IsActive = candidate == tab;
        }

        ActiveTab = tab;

        if (ActiveSpace is not null)
        {
            ActiveSpace.LastActiveTab = tab;
        }

        ActiveTabChanged?.Invoke(this, EventArgs.Empty);
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

    private async Task ConnectSpaceAsync(SpaceViewModel space)
    {
        IsBusy = true;
        StatusText = $"Connecting to {space.Name}…";

        try
        {
            await space.Connection.ConnectAsync(_lifetime.Token);
            var databases = await space.Connection.GetDatabasesAsync(_lifetime.Token);
            space.UpdateDatabases(databases);
            ApplyDatabase(space.ActiveDatabase);

            if (ActiveTab is not null && space == ActiveSpace)
            {
                ActiveTab.Database = space.ActiveDatabase?.Name ?? string.Empty;
            }

            await RefreshSchemaAsync(space);
            StatusText = $"Connected · {space.Connection.Endpoint} · {databases.Count} databases";
        }
        catch (Exception ex)
        {
            StatusText = "Connection failed";
            LastResult = QueryResult.Error(ex.Message, TimeSpan.Zero);
            ResultMessage = ex.Message;
            ResultSummary = string.Empty;
            ResultChanged?.Invoke(this, EventArgs.Empty);
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
            _schema = [];
            OnPropertyChanged(nameof(Tables));
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

    private async Task RefreshSchemaAsync(SpaceViewModel space)
    {
        try
        {
            var schema = await space.Connection.GetSchemaAsync(space.ActiveDatabase?.Name, _lifetime.Token);
            if (space == ActiveSpace)
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
