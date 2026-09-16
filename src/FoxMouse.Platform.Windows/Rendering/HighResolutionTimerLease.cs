using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Rendering;

public sealed class HighResolutionTimerLease : IDisposable
{
    private bool _active;

    public HighResolutionTimerLease()
    {
        _active = NativeMethods.TimeBeginPeriod(1) == 0;
    }

    public void Dispose()
    {
        if (_active)
        {
            _ = NativeMethods.TimeEndPeriod(1);
            _active = false;
        }
    }
}
