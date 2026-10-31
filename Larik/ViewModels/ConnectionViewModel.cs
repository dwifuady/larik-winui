using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>
/// The single connection a space owns. A connection reaches a server and exposes several databases;
/// its state (driver, host, connected) is therefore per space, never per database.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    public ConnectionViewModel(string driver, string host, string user, bool isConnected)
    {
        Driver = driver;
        Host = host;
        User = user;
        IsConnected = isConnected;
    }

    public string Driver { get; }
    public string Host { get; }
    public string User { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial bool IsConnected { get; set; }

    public string DriverLabel => Driver switch
    {
        "mssql" => "SQL Server",
        "postgres" => "PostgreSQL",
        "sqlite" => "SQLite",
        _ => Driver,
    };

    public string StatusText => IsConnected ? "Connected" : "Disconnected";

    public SolidColorBrush StatusBrush => IsConnected ? StudioColors.Success : StudioColors.Warning;

    public string Endpoint => $"{DriverLabel} · {Host}";

    /// <summary>Status is colour AND text — never colour alone.</summary>
    public string StatusLine => $"{Endpoint} · {StatusText}";
}
