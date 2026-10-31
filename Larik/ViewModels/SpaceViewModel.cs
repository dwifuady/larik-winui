using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>
/// A space is a workspace: it groups related databases and owns the tabs opened against them.
/// The space's active database supplies the connection context shown in the rail and status bar.
/// </summary>
public partial class SpaceViewModel : ObservableObject
{
    public SpaceViewModel(string id, string name, string colorHex)
    {
        Id = id;
        ColorHex = colorHex;
        AccentBrush = StudioColors.Brush(colorHex);
        ActiveTintBrush = StudioColors.Tint(colorHex);
        Name = name;
    }

    public string Id { get; }
    public string ColorHex { get; }
    public SolidColorBrush AccentBrush { get; }
    public SolidColorBrush ActiveTintBrush { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveDotVisible))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLine))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectedDatabaseText))]
    [NotifyPropertyChangedFor(nameof(Driver))]
    [NotifyPropertyChangedFor(nameof(DatabaseCountLabel))]
    public partial DatabaseViewModel? ActiveDatabase { get; set; }

    public ObservableCollection<DatabaseViewModel> Databases { get; } = [];

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public DatabaseViewModel? LastActiveDatabase { get; set; }

    public TabViewModel? LastActiveTab { get; set; }

    public string Driver => ActiveDatabase?.Driver ?? "mssql";

    public string DatabaseCountLabel => Databases.Count == 1 ? "1 database" : $"{Databases.Count} databases";

    public string TooltipText => $"{Name} — {DatabaseCountLabel}";

    public string StatusText => ActiveDatabase?.StatusText ?? "Disconnected";

    public SolidColorBrush StatusBrush => ActiveDatabase?.StatusBrush ?? StudioColors.Warning;

    /// <summary>Status is colour AND text — never colour alone.</summary>
    public string ConnectionLine => ActiveDatabase is null
        ? "No database"
        : $"{ActiveDatabase.DriverLabel} · {ActiveDatabase.Host}";

    public string ConnectedDatabaseText => ActiveDatabase?.Name ?? "no database";

    public double IsActiveDotVisible => IsActive ? 1 : 0;
}
