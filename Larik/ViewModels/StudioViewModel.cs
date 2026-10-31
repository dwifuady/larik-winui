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
    private int _queryCounter;
    private IReadOnlyList<TableSchema> _schema = [];

    public StudioViewModel()
    {
        StatusText = "Ready";
        CaretText = "Ln 1, Col 1";
        ResultMessage = string.Empty;
        ResultSummary = string.Empty;
        IsSidebarOpen = true;

        Seed();
        SelectSpace(Spaces[0]);
        ResultMessage = "Connect the workspace, then run a query with Ctrl+Enter";
    }

    public ObservableCollection<SpaceViewModel> Spaces { get; } = [];

    /// <summary>Tabs of the active space. Mutated in place so the ListView keeps its identity.</summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarVisibility))]
    [NotifyPropertyChangedFor(nameof(SidebarToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(IsSidebarCollapsed))]
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

    /// <summary>When the rail is hidden the caption shows its own pane toggle, so the action stays reachable.</summary>
    public bool IsSidebarCollapsed => !IsSidebarOpen;

    public string SidebarToggleGlyph => IsSidebarOpen ? "\uE8A0" : "\uE89F";

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

    public void UpdateCaret(string text, int position)
    {
        var before = text.Length == 0 ? string.Empty : text[..Math.Clamp(position, 0, text.Length)];
        var line = before.Count(c => c == '\n') + 1;
        var column = before.Length - (before.LastIndexOf('\n') + 1) + 1;
        CaretText = $"Ln {line}, Col {column}";
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
