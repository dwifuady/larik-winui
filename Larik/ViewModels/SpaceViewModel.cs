using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
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
        Connection.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(ConnectionLine));
            OnPropertyChanged(nameof(Driver));
        };
    }

    public string Id { get; }
    public string ColorHex { get; }
    public SolidColorBrush AccentBrush { get; }
    public SolidColorBrush ActiveTintBrush { get; }
    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveDotVisible))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedDatabaseText))]
    public partial DatabaseViewModel? ActiveDatabase { get; set; }

    public ObservableCollection<DatabaseViewModel> Databases { get; } = [];

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public DatabaseViewModel? LastActiveDatabase { get; set; }

    public TabViewModel? LastActiveTab { get; set; }

    public string Driver => Connection.Driver;

    public string DatabaseCountLabel => Databases.Count == 1 ? "1 database" : $"{Databases.Count} databases";

    public string TooltipText => $"{Name} — {Connection.Endpoint} — {DatabaseCountLabel}";

    public string StatusText => Connection.StatusText;

    public SolidColorBrush StatusBrush => Connection.StatusBrush;

    public string ConnectionLine => Connection.StatusLine;

    public string ConnectedDatabaseText => ActiveDatabase?.Name ?? "no database";

    public double IsActiveDotVisible => IsActive ? 1 : 0;
}
