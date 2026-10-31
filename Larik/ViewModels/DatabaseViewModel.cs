using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Larik.ViewModels;

/// <summary>One database inside a space. A space is a workspace that can hold several.</summary>
public partial class DatabaseViewModel : ObservableObject
{
    public DatabaseViewModel(string name, string driver, string host, bool isConnected)
    {
        Name = name;
        Driver = driver;
        Host = host;
        IsConnected = isConnected;
    }

    public string Name { get; }
    public string Driver { get; }
    public string Host { get; }

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameWeight))]
    [NotifyPropertyChangedFor(nameof(CheckVisibility))]
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

    /// <summary>Second line in the database picker: driver and host.</summary>
    public string Summary => $"{DriverLabel} · {Host}";

    public FontWeight NameWeight => IsActive
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;

    public Visibility CheckVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
}
