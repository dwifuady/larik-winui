using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Larik.Controls;

/// <summary>A draggable title strip for the custom-reference card. <c>ProtectedCursor</c> is only reachable from a subclass.</summary>
public sealed partial class MoveHandle : Grid
{
    public MoveHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }
}
