namespace FoxMouse.Platform.Windows.Visibility;

public sealed class FakeSystemCursorController : ISystemCursorController
{
    private bool _disposed;

    public bool IsInitialized { get; private set; }

    public bool IsHiddenByThisController { get; private set; }

    public bool FailInitialization { get; set; }

    public bool FailNextTransition { get; set; }

    public List<bool> VisibilityTransitions { get; } = [];

    public bool TryInitialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsInitialized = !FailInitialization;
        return IsInitialized;
    }

    public bool TrySetVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryInitialize() || FailNextTransition)
        {
            FailNextTransition = false;
            return false;
        }

        VisibilityTransitions.Add(visible);
        IsHiddenByThisController = !visible;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (IsHiddenByThisController)
        {
            VisibilityTransitions.Add(true);
            IsHiddenByThisController = false;
        }

        _disposed = true;
    }
}
