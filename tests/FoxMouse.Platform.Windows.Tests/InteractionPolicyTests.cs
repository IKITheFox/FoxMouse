using System.Drawing;
using FoxMouse.Core;
using FoxMouse.Platform.Windows.Policy;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class InteractionPolicyTests
{
    [Fact]
    public void ButtonDownWithoutThresholdMovementIsNotDragging()
    {
        DragClassifier classifier = new();

        Assert.False(classifier.ObservePosition(100, 100, PointerButtons.Left, 4, 4));
        Assert.False(classifier.ObservePosition(103, 97, PointerButtons.Left, 4, 4));
        Assert.False(classifier.ObservePosition(103, 97, PointerButtons.None, 4, 4));
        Assert.False(classifier.IsDragging);
    }

    [Fact]
    public void HeldButtonBecomesDragOnlyAfterCrossingConfiguredThreshold()
    {
        DragClassifier classifier = new();

        _ = classifier.ObserveDelta(0, 0, PointerButtons.Left, 4_000, 4_000);
        Assert.False(classifier.ObserveDelta(3_999, 0, PointerButtons.Left, 4_000, 4_000));
        Assert.True(classifier.ObserveDelta(1, 0, PointerButtons.Left, 4_000, 4_000));
        Assert.True(classifier.ObserveDelta(-4_000, 0, PointerButtons.Left, 4_000, 4_000));
        Assert.False(classifier.ObserveDelta(0, 0, PointerButtons.None, 4_000, 4_000));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(8, 4)]
    public void DragMetricIsConvertedFromCenteredRectangleToRadius(int metric, int expected)
    {
        Assert.Equal(expected, InteractionPolicy.GetDragRadiusPixels(metric));
    }

    [Fact]
    public void PolicyConsumesNormalizedMotionAndResetsOnButtonRelease()
    {
        InteractionPolicy policy = new();
        policy.ObservePointer(new MotionSample(
            1,
            0,
            0,
            0,
            PointerButtons.Left,
            MotionSampleFlags.DeviceChanged));
        Assert.False(policy.IsPointerDragging);

        policy.ObservePointer(new MotionSample(
            2,
            0,
            1_000_000,
            0,
            PointerButtons.Left));
        Assert.True(policy.IsPointerDragging);

        policy.ObservePointer(new MotionSample(
            3,
            0,
            0,
            0,
            PointerButtons.None));
        Assert.False(policy.IsPointerDragging);
    }

    [Fact]
    public void IsFullScreenRejectsNullAndInvalidHandles()
    {
        Assert.False(InteractionPolicy.IsFullScreen(nint.Zero));
        Assert.False(InteractionPolicy.IsFullScreen(new nint(-1)));
    }

    [WindowsDesktopFact]
    public void IsFullScreenRecognizesNeverShownScreenSizedWindow()
    {
        StaTestThread.Run(() =>
        {
            Rectangle monitorBounds = Screen.PrimaryScreen?.Bounds
                ?? throw new InvalidOperationException("No primary screen is available.");
            using Form form = new()
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = monitorBounds,
            };

            _ = form.Handle;

            Assert.True(InteractionPolicy.IsFullScreen(form.Handle));
        });
    }

    [WindowsDesktopFact]
    public void IsFullScreenRejectsNeverShownSmallWindow()
    {
        StaTestThread.Run(() =>
        {
            using Form form = new()
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(10, 10, 32, 32),
            };

            _ = form.Handle;

            Assert.False(InteractionPolicy.IsFullScreen(form.Handle));
        });
    }

    [WindowsDesktopFact]
    public void EvaluateWithOptionalBlockingDisabledCannotReportThoseReasons()
    {
        InteractionPolicy policy = new();

        TriggerBlockReason result = policy.Evaluate(
            disableWhileDragging: false,
            pauseInFullScreen: false,
            excludedProcesses: null);

        Assert.Contains(
            result,
            new[]
            {
                TriggerBlockReason.None,
                TriggerBlockReason.RemoteSession,
                TriggerBlockReason.WindowsMagnifier,
                TriggerBlockReason.NoForegroundWindow,
            });
    }

    [WindowsDesktopFact]
    public void ShellDesktopIsNotClassifiedAsExclusiveFullScreen()
    {
        nint shellWindow = FoxMouse.Platform.Windows.Interop.NativeMethods.GetShellWindow();
        if (shellWindow != nint.Zero)
        {
            Assert.False(InteractionPolicy.IsFullScreen(shellWindow));
        }
    }
}
