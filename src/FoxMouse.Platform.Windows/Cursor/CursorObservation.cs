using System.Drawing;

namespace FoxMouse.Platform.Windows.Cursor;

public readonly record struct CursorObservation(
    Point Position,
    bool IsVisible,
    CursorImage? Image);
