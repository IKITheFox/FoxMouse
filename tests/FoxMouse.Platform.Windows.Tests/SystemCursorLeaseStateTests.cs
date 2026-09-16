using FoxMouse.Platform.Windows.Visibility;
using static FoxMouse.Platform.Windows.Tests.MagnificationCursorControllerTests;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class SystemCursorLeaseStateTests
{
    [Fact]
    public void LeaseExpiryRestoresExactlyOnceAndRepeatedShowIsIdempotent()
    {
        long now = 10_000;
        FakeMagnificationApi api = new();
        using MagnificationCursorController controller = new(api);
        SystemCursorLeaseState lease = new(controller, () => now);

        Assert.True(lease.TryHide(generation: 41, leaseMilliseconds: 200));
        long deadline = lease.LeaseDeadline;
        lease.RestoreIfLeaseExpired(deadline - 1);
        Assert.True(lease.IsHidden);

        lease.RestoreIfLeaseExpired(deadline);

        Assert.False(lease.IsHidden);
        Assert.Equal([false, true], api.VisibilityTransitions);

        Assert.True(lease.TryShow());
        Assert.Equal([false, true], api.VisibilityTransitions);
    }

    [Fact]
    public void FailedLeaseRestoreRetriesAfterBoundedDelay()
    {
        long now = 20_000;
        FakeMagnificationApi api = new();
        using MagnificationCursorController controller = new(api);
        SystemCursorLeaseState lease = new(controller, () => now);
        Assert.True(lease.TryHide(generation: 73, leaseMilliseconds: 200));
        now = lease.LeaseDeadline;
        api.FailNextVisibilityTransition = true;

        lease.RestoreIfLeaseExpired(now);

        Assert.True(lease.IsHidden);
        long retryDeadline = lease.LeaseDeadline;
        Assert.Equal(now + SystemCursorLeaseState.MillisecondsToTicks(100), retryDeadline);

        lease.RestoreIfLeaseExpired(retryDeadline);

        Assert.False(lease.IsHidden);
        Assert.Equal([false, true], api.VisibilityTransitions);
    }
}
