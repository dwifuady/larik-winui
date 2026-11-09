using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Data;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace Larik.ViewModels;

/// <summary>A database exposed by the space's connection. It carries no connection state of its own.</summary>
public sealed partial class DatabaseViewModel : ObservableObject
{
    public DatabaseViewModel(DatabaseInfo info) => Info = info;

    public DatabaseInfo Info { get; }

    public string Name => Info.Name;

    /// <summary>False when the login can see the name but cannot open it; it sorts after the usable ones.</summary>
    public bool IsAccessible => Info.IsAccessible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameWeight))]
    [NotifyPropertyChangedFor(nameof(CheckVisibility))]
    public partial bool IsActive { get; set; }

    public FontWeight NameWeight => IsActive
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;

    public Visibility CheckVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
}
