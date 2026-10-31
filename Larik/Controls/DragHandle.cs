using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Larik.Controls;

/// <summary>A thin resize handle. <c>ProtectedCursor</c> is only reachable from a subclass.</summary>
public sealed partial class DragHandle : Grid
{
    public DragHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
