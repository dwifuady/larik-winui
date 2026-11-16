using Microsoft.UI.Xaml.Controls;

namespace Larik.Controls;

/// <summary>
/// The pan puck <see cref="MiddleClickAutoScroll"/> parks at the press point. It is decoration with
/// no input or automation surface of its own; the host owns when it is visible.
/// </summary>
public sealed partial class AutoscrollIndicator : UserControl
{
    public AutoscrollIndicator()
    {
        InitializeComponent();
    }
}
