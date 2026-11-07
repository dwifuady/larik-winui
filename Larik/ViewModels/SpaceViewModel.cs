using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>
/// A space is a workspace with exactly one connection. That connection exposes several databases;
/// the space's active database supplies the query context shown in the rail, toolbar and status bar.
/// </summary>
public partial class SpaceViewModel : ObservableObject
{
    public SpaceViewModel(string id, string name, string colorHex, ConnectionViewModel connection)
    {
        Id = id;
        ColorHex = colorHex;
        AccentBrush = StudioColors.Brush(colorHex);
        ActiveTintBrush = StudioColors.Tint(colorHex);
        Name = name;
        Connection = connection;

        // Connection state belongs to the space, so its changes surface as space changes.
        Connection.PropertyChanged += (_, args) =>
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(ConnectionLine));
            OnPropertyChanged(nameof(Driver));
            OnPropertyChanged(nameof(ConnectedDatabaseText));
        };
    }

    public string Id { get; }
    public string ColorHex { get; }
    public SolidColorBrush AccentBrush { get; }
    public SolidColorBrush ActiveTintBrush { get; }
    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    [NotifyPropertyChangedFor(nameof(IndicatorName))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveDotVisible))]
    [NotifyPropertyChangedFor(nameof(IndicatorDotSize))]
    [NotifyPropertyChangedFor(nameof(IndicatorDotOpacity))]
    [NotifyPropertyChangedFor(nameof(IndicatorRingVisibility))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedDatabaseText))]
    public partial DatabaseViewModel? ActiveDatabase { get; set; }

    public ObservableCollection<DatabaseViewModel> Databases { get; } = [];

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public DatabaseViewModel? LastActiveDatabase { get; set; }

    public TabViewModel? LastActiveTab { get; set; }

    /// <summary>
    /// Database to reselect when the connection opens. It outlives disconnects (the database
    /// list empties) and restarts, so a space remembers what the user was working on.
    /// </summary>
    public string? PreferredDatabase { get; set; }

    public bool IsConnected => Connection.IsConnected;

    public string Driver => Connection.Dialect;

    public string DatabaseCountLabel => Databases.Count == 1 ? "1 database" : $"{Databases.Count} databases";

    public string TooltipText => $"{Name} — {Connection.Endpoint} — {DatabaseCountLabel}";

    public string StatusText => Connection.StatusText;

    public SolidColorBrush StatusBrush => Connection.StatusBrush;

    public string ConnectionLine => Connection.StatusLine;

    public string ConnectedDatabaseText => ActiveDatabase?.Name
        ?? (Connection.IsConnected ? "no database" : "not connected");

    public double IsActiveDotVisible => IsActive ? 1 : 0;

    // ---- workspace indicator (bottom of the rail): one dot per workspace, the active one ringed

    public string IndicatorName => $"Workspace {Name}";

    public double IndicatorDotSize => IsActive ? 11 : 7;

    public double IndicatorDotOpacity => IsActive ? 1 : 0.45;

    public Visibility IndicatorRingVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Replaces the database list with what the server reported, keeping the selection by name.</summary>
    public void UpdateDatabases(IEnumerable<string> names)
    {
        var selectedName = ActiveDatabase?.Name ?? PreferredDatabase;
        var incoming = names.ToList();

        Databases.Clear();
        foreach (var name in incoming)
        {
            Databases.Add(new DatabaseViewModel(name));
        }

        ActiveDatabase = Databases.FirstOrDefault(d => string.Equals(d.Name, selectedName, StringComparison.OrdinalIgnoreCase))
            ?? Databases.FirstOrDefault();

        if (ActiveDatabase is not null)
        {
            ActiveDatabase.IsActive = true;
        }

        LastActiveDatabase = ActiveDatabase;
        OnPropertyChanged(nameof(DatabaseCountLabel));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(ConnectedDatabaseText));
    }
}
