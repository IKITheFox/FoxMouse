using FoxMouse.App;

namespace FoxMouse.Settings.Tests;

public sealed class FoxMouseEngineSafetyTimingTests
{
    [Theory]
    [InlineData(299_999, 300_000, 0, false)]
    [InlineData(300_000, 300_000, 0, true)]
    [InlineData(500_000, 300_000, 0.01, false)]
    [InlineData(500_000, 300_000, 1, false)]
    public void ReleaseCannotBypassMinimumVisibleDeadline(
        long now,
        long visibleUntil,
        double detectorScore,
        bool expected)
    {
        Assert.Equal(
            expected,
            FoxMouseEngine.ShouldRequestRelease(now, visibleUntil, detectorScore));
    }

    [Theory]
    [InlineData(1_000_000, 7, 7, 500_000, true, true)]
    [InlineData(1_000_001, 7, 7, 500_000, true, false)]
    [InlineData(900_000, 7, 7, 1_000_000, true, true)]
    [InlineData(1_000_000, 7, 8, 900_000, true, false)]
    [InlineData(1_000_000, 7, 7, -1, true, false)]
    [InlineData(1_000_000, 7, 7, 900_000, false, false)]
    public void GuardRenewalRequiresCurrentSuccessfulRenderHeartbeat(
        long now,
        long generation,
        long heartbeatGeneration,
        long heartbeat,
        bool replacementActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            FoxMouseEngine.IsRenderHeartbeatFresh(
                now,
                generation,
                heartbeatGeneration,
                heartbeat,
                replacementActive));
    }

    [Theory]
    [InlineData(1_499_999, 11, 11, 1_000_000, true, true)]
    [InlineData(1_500_000, 11, 11, 1_000_000, true, true)]
    [InlineData(1_500_001, 11, 11, 1_000_000, true, false)]
    [InlineData(1_100_000, 11, 12, 1_000_000, true, false)]
    [InlineData(1_100_000, 11, 11, -1, true, false)]
    [InlineData(1_100_000, 11, 11, 1_000_000, false, false)]
    public void PendingHideAcknowledgementGetsOnlyABoundedHandoffWindow(
        long now,
        long generation,
        long pendingGeneration,
        long pendingTimestamp,
        bool pending,
        bool expected)
    {
        Assert.Equal(
            expected,
            FoxMouseEngine.IsHideHandoffPending(
                now,
                generation,
                pendingGeneration,
                pendingTimestamp,
                pending));
    }
}
