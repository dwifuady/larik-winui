using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>A space owns exactly one connection (the desktop app's 1:1 model).</summary>
public partial class SpaceViewModel : ObservableObject
{
    public SpaceViewModel(
        string id,
        string name,
        string colorHex,
        string driver,
        string host,
        string database,
        string username,
        bool isConnected)
    {
        Id = id;
        ColorHex = colorHex;
        Driver = driver;
        Host = host;
        Username = username;
        AccentBrush = StudioColors.Brush(colorHex);
        ActiveTintBrush = StudioColors.Tint(colorHex);
        Name = name;
        Database = database;
        IsConnected = isConnected;
    }

    public string Id { get; }
    public string ColorHex { get; }
    public string Driver { get; }
    public string Host { get; }
    public string Username { get; }
    public SolidColorBrush AccentBrush { get; }
    public SolidColorBrush ActiveTintBrush { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedDatabaseText))]
    public partial string Database { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectionLine))]
    [NotifyPropertyChangedFor(nameof(ConnectedDatabaseText))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveDotVisible))]
    public partial bool IsActive { get; set; }

    public string DriverLabel => Driver switch
    {
        "mssql" => "SQL Server",
        "postgres" => "PostgreSQL",
        "sqlite" => "SQLite",
        _ => Driver,
    };

    public string StatusText => IsConnected ? "Connected" : "Disconnected";

    public SolidColorBrush StatusBrush => IsConnected ? StudioColors.Success : StudioColors.Warning;

    /// <summary>Status is colour AND text — never colour alone.</summary>
    public string ConnectionLine => IsConnected
        ? $"{DriverLabel} · {Host}"
        : $"{DriverLabel} · {Host} · disconnected";

    public string ConnectedDatabaseText => IsConnected ? Database : "not connected";

    public string TooltipText => $"{Name} — {ConnectionLine}";

    public double IsActiveDotVisible => IsActive ? 1 : 0;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public TabViewModel? LastActiveTab { get; set; }
}
