using System.Drawing;
using FoxMouse.Platform.Windows.Cursor;

namespace FoxMouse.Platform.Windows.Rendering;

public interface IOverlayController : IDisposable
{
    bool IsVisible { get; }

    void ShowCursor(
        CursorObservation cursor,
        double scale,
        byte opacity = 255,
        double preparedMaximumScale = 1.0);

    void ShowLocator(Point position, double progress, byte opacity = 210);

    void HideOverlay();
}
