namespace FoxMouse.Platform.Windows.Visibility;

public interface ISystemCursorController : IDisposable
{
    bool IsInitialized { get; }

    bool IsHiddenByThisController { get; }

    bool TryInitialize();

    bool TrySetVisible(bool visible);
}
