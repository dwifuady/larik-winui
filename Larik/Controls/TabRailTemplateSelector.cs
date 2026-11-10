using Larik.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Larik.Controls;

/// <summary>
/// The rail is one flat list per band so drag-reorder and scrolling stay native. This turns the
/// mixed items into a group header or a tab row.
/// </summary>
public sealed class TabRailTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TabTemplate { get; set; }

    public DataTemplate? GroupTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is TabGroupViewModel ? GroupTemplate : TabTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
