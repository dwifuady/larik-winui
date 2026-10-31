using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Larik.ViewModels;

/// <summary>One vertical tab in the sidebar.</summary>
public partial class TabViewModel : ObservableObject
{
    private bool _isPointerOver;

    public TabViewModel(string id, string title, string? database, SpaceViewModel space, string content)
    {
        Id = id;
        Space = space;
        Title = title;
        Database = database ?? string.Empty;
        Content = content;
    }

    public string Id { get; }

    public SpaceViewModel Space { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(SubtitleVisibility))]
    public partial string Database { get; set; }

    [ObservableProperty]
    public partial string Content { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTintBrush))]
    [NotifyPropertyChangedFor(nameof(ActiveStripOpacity))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    [NotifyPropertyChangedFor(nameof(SubtitleVisibility))]
    [NotifyPropertyChangedFor(nameof(CloseButtonVisibility))]
    public partial bool IsActive { get; set; }

    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            if (SetProperty(ref _isPointerOver, value))
            {
                OnPropertyChanged(nameof(SubtitleVisibility));
                OnPropertyChanged(nameof(CloseButtonVisibility));
            }
        }
    }

    public string Subtitle => string.IsNullOrWhiteSpace(Database) ? Space.Name : Database;

    /// <summary>Database label reveals on hover or when the tab is active (Arc behaviour).</summary>
    public Visibility SubtitleVisibility => IsActive || IsPointerOver ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CloseButtonVisibility => IsActive || IsPointerOver ? Visibility.Visible : Visibility.Collapsed;

    public Brush? ActiveTintBrush => IsActive ? Space.ActiveTintBrush : null;

    public double ActiveStripOpacity => IsActive ? 1 : 0;

    public FontWeight TitleWeight => IsActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public SolidColorBrush AccentBrush => Space.AccentBrush;
}
