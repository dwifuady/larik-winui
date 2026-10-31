using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Xaml;

namespace Larik.ViewModels;

/// <summary>Shell state: spaces, their tabs, and the active query session.</summary>
public partial class StudioViewModel : ObservableObject
{
    private int _queryCounter;

    public StudioViewModel()
    {
        StatusText = "Ready";
        CaretText = "Ln 1, Col 1";
        ResultMessage = string.Empty;
        ResultSummary = string.Empty;
        IsSidebarOpen = true;

        Seed();
        SelectSpace(Spaces[0]);
        ResultMessage = "Run a query with Ctrl+Enter";
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

    public Visibility SidebarVisibility => IsSidebarOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>When the rail is hidden the caption shows its own pane toggle, so the action stays reachable.</summary>
    public bool IsSidebarCollapsed => !IsSidebarOpen;

    public string SidebarToggleGlyph => IsSidebarOpen ? "\uE8A0" : "\uE89F";

    public string WindowTitle => ActiveTab is null ? "Larik SQL Studio" : $"{ActiveTab.Title} — Larik";

    public IReadOnlyList<SnippetDef> Snippets => SnippetCatalog.ForDriver(ActiveSpace?.Driver ?? "mssql");

    public IReadOnlyList<TableDef> Tables => MockDatabase.Tables;

    public ResultSet? LastResult { get; private set; }

    public event EventHandler? ResultChanged;

    public event EventHandler? ActiveTabChanged;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

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
            ActiveSpace.Database,
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
        var space = new SpaceViewModel(
            Guid.NewGuid().ToString("n"),
            $"Space {index + 1}",
            color,
            "mssql",
            "localhost",
            "untitled",
            "sa",
            isConnected: false);

        var tab = new TabViewModel(Guid.NewGuid().ToString("n"), "Query 1", space.Database, space, string.Empty);
        space.Tabs.Add(tab);
        Spaces.Add(space);
        SelectSpace(space);
        StatusText = "New space — configure the connection when the driver layer lands";
    }

    [RelayCommand]
    private void Run()
    {
        if (ActiveTab is null)
        {
            return;
        }

        var sql = ActiveTab.Content;
        var upper = sql.ToUpperInvariant();
        var mutates = upper.Contains("UPDATE ") || upper.Contains("DELETE ") || upper.Contains("TRUNCATE ");
        var guarded = upper.Contains("WHERE");

        if (mutates && !guarded)
        {
            LastResult = new ResultSet
            {
                Columns = [],
                Rows = [],
                Message = "Refused: this statement modifies rows without a WHERE clause.",
                IsError = true,
            };
            FinishRun();
            return;
        }

        LastResult = MockDatabase.Execute(sql);
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

    private void FinishRun()
    {
        if (LastResult is null)
        {
            return;
        }

        ResultMessage = LastResult.Message;
        ResultSummary = LastResult is { IsError: false, Rows.Count: > 0 }
            ? $"{LastResult.Rows.Count} rows · {LastResult.ElapsedMs} ms"
            : string.Empty;

        StatusText = LastResult.IsError ? "Error" : $"{LastResult.Rows.Count} rows returned";
        ResultChanged?.Invoke(this, EventArgs.Empty);
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

        SelectTab(tab ?? space.LastActiveTab ?? space.Tabs.FirstOrDefault());
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
            var replacement = new TabViewModel(Guid.NewGuid().ToString("n"), $"Query {++_queryCounter}", space.Database, space, string.Empty);
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

    private void Seed()
    {
        var prod = new SpaceViewModel("space-prod", "Larik Production", StudioColors.Palette[0], "mssql", "prod-sql-01", "larik_prod", "larik_app", isConnected: true);
        prod.Tabs.Add(new TabViewModel("t1", "Revenue by month", "larik_prod", prod,
            "SELECT\n    DATEFROMPARTS(YEAR(ordered_at), MONTH(ordered_at), 1) AS month,\n    SUM(total_amount) AS revenue\nFROM orders\nWHERE status = 'paid'\nGROUP BY DATEFROMPARTS(YEAR(ordered_at), MONTH(ordered_at), 1)\nORDER BY month DESC;"));
        prod.Tabs.Add(new TabViewModel("t2", "Slow customers", "larik_prod", prod,
            "SELECT c.full_name, COUNT(*) AS orders_count\nFROM customers c\nINNER JOIN orders o ON o.customer_id = c.id\nGROUP BY c.full_name\nORDER BY orders_count ASC;"));
        prod.Tabs.Add(new TabViewModel("t3", "Scratch", "larik_prod", prod, "SELECT * FROM customers;"));

        var staging = new SpaceViewModel("space-staging", "Staging", StudioColors.Palette[2], "postgres", "staging-pg", "larik_staging", "larik", isConnected: true);
        staging.Tabs.Add(new TabViewModel("t4", "Migration check", "larik_staging", staging,
            "SELECT table_name\nFROM information_schema.tables\nWHERE table_schema = 'public';"));

        var local = new SpaceViewModel("space-local", "Local playground", StudioColors.Palette[3], "sqlite", "local file", "playground.db", "-", isConnected: false);
        local.Tabs.Add(new TabViewModel("t5", "Notes", null, local,
            "-- Local SQLite playground\n-- Connect to start querying\nSELECT 1;"));

        Spaces.Add(prod);
        Spaces.Add(staging);
        Spaces.Add(local);
    }
}
