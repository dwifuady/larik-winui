using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>
/// A named, coloured set of tabs. The same object is a group in the TABS section and, once
/// pinned, a folder in the pinned band: pinning is a property, not a second container type.
/// </summary>
public partial class TabGroupViewModel : ObservableObject
{
    public TabGroupViewModel(string id, string name, string colorHex, bool isPinned, string? database = null)
    {
        Id = id;
        Name = name;
        ColorHex = colorHex;
        IsPinned = isPinned;
        Database = database;

        // The header count is the only place the size is shown, so keep it live rather than
        // asking every caller to refresh the rail after moving a tab.
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountLabel));
            OnPropertyChanged(nameof(AutomationName));
        };
    }

    public string Id { get; }

    /// <summary>
    /// The database this group was created from, when it came from "Group by database". New tabs
    /// opened on that database join the group without the user choosing a destination. Renaming
    /// the group does not change the association.
    /// </summary>
    public string? Database { get; set; }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentBrush))]
    public partial string ColorHex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    [NotifyPropertyChangedFor(nameof(PinVisibility))]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    [NotifyPropertyChangedFor(nameof(CollapseLabel))]
    public partial bool IsExpanded { get; set; } = true;

    public string CountLabel => Tabs.Count.ToString();

    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public string CollapseLabel => IsExpanded ? "Collapse" : "Expand";

    public string PinLabel => IsPinned ? "Unpin group" : "Pin group";

    public string AutomationName =>
        $"{Name}, {Tabs.Count} tab{(Tabs.Count == 1 ? string.Empty : "s")}, {(IsExpanded ? "expanded" : "collapsed")}";

    public Visibility PinVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Group headers are lighter than tab rows so the tabs stay the content.</summary>
    public double RowHeight => IsPinned ? 34 : 32;

    public SolidColorBrush AccentBrush => StudioColors.Brush(ColorHex);
}
