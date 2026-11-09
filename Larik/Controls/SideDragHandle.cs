using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Larik.Controls;

/// <summary>A thin vertical resize handle (east-west cursor). <c>ProtectedCursor</c> is only reachable from a subclass.</summary>
public sealed partial class SideDragHandle : Grid
{
    public SideDragHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
