using FoxMouse.Platform.Windows.Cursor;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class CursorTrackerDesktopTests
{
    [WindowsDesktopFact]
    public void ObserveCurrentCursorReturnsSaneReadOnlySnapshot()
    {
        // CursorTracker only calls observation and icon-copy APIs. This smoke test
        // deliberately never constructs a system-cursor visibility controller.
        using CursorTracker tracker = new();

        Assert.True(tracker.TryObserve(out CursorObservation observation));

        if (observation.Image is not null)
        {
            Assert.True(observation.IsVisible);
            Assert.True(observation.Image.Bitmap.Width > 0);
            Assert.True(observation.Image.Bitmap.Height > 0);
            Assert.True(observation.Image.DisplaySize.Width > 0);
            Assert.True(observation.Image.DisplaySize.Height > 0);
            Assert.True(observation.Image.ResolutionScale > 0d);
            Assert.True(Enum.IsDefined(observation.Image.RenderQuality));

            if (observation.Image.SupportsReplacement)
            {
                Assert.InRange(observation.Image.Hotspot.X, 0, observation.Image.Bitmap.Width - 1);
                Assert.InRange(observation.Image.Hotspot.Y, 0, observation.Image.Bitmap.Height - 1);
            }
        }
        else if (!observation.IsVisible)
        {
            Assert.Null(observation.Image);
        }

        tracker.Invalidate();
        Assert.True(tracker.TryObserve(out _));
    }

    [WindowsDesktopFact]
    public void ObserveAfterDisposeThrowsWithoutChangingCursorVisibility()
    {
        CursorTracker tracker = new();
        Assert.True(tracker.TryObserve(out _));

        tracker.Dispose();

        Assert.Throws<ObjectDisposedException>(() => tracker.TryObserve(out _));
    }

    [WindowsDesktopFact]
    public void RequestedEightTimesResolutionReturnsBoundedQualityMetadata()
    {
        // Observation and raster-copy only: this never changes the native
        // cursor visibility and is safe to run during interactive validation.
        using CursorTracker tracker = new();

        Assert.True(tracker.TryObserve(out CursorObservation observation, requestedMaximumScale: 8d));
        if (observation.Image is null)
        {
            return;
        }

        CursorImage image = observation.Image;
        Assert.InRange(image.Bitmap.Width, 1, 2_048);
        Assert.InRange(image.Bitmap.Height, 1, 2_048);
        Assert.InRange(image.ResolutionScale, double.Epsilon, 8d);
        Assert.True(Enum.IsDefined(image.RenderQuality));
    }
}
