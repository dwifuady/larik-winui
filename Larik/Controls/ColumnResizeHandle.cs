using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Larik.Controls;

/// <summary>The draggable strip at a header cell's right edge; shows the horizontal resize cursor.</summary>
public sealed class ColumnResizeHandle : Grid
{
    public ColumnResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
