using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace Larik.ViewModels;

/// <summary>A database exposed by the space's connection. It carries no connection state of its own.</summary>
public sealed partial class DatabaseViewModel : ObservableObject
{
    public DatabaseViewModel(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameWeight))]
    [NotifyPropertyChangedFor(nameof(CheckVisibility))]
    public partial bool IsActive { get; set; }

    public FontWeight NameWeight => IsActive
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;

    public Visibility CheckVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
}
