using FoxMouse.Platform.Windows.Rendering;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class HighResolutionTimerLeaseTests
{
    [WindowsDesktopFact]
    public void LeaseCanAcquireAndReleaseTheWindowsTimerResolution()
    {
        using HighResolutionTimerLease lease = new();
    }
}
