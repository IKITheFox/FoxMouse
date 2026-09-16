namespace FoxMouse.Platform.Windows.Visibility;

/// <summary>
/// Requests an immediate redraw of the visible system cursor without moving it.
/// </summary>
public interface ISystemCursorRefresher
{
    bool TryRefreshAtCurrentPosition();
}
