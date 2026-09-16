using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class HotspotMathTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.5)]
    [InlineData(6.0)]
    public void ScalingKeepsHotspotAtRealPointer(double scale)
    {
        var cursor = new PointD(800, 450);
        var hotspot = new PointD(4, 7);

        var bounds = HotspotMath.CalculateBounds(cursor, hotspot, 32, 32, scale);
        var reconstructed = HotspotMath.ReconstructHotspot(bounds, hotspot, scale);

        Assert.Equal(cursor.X, reconstructed.X, 10);
        Assert.Equal(cursor.Y, reconstructed.Y, 10);
    }

    [Fact]
    public void NegativeVirtualCoordinatesArePreserved()
    {
        var bounds = HotspotMath.CalculateBounds(
            new PointD(-100, -25),
            new PointD(3, 5),
            32,
            48,
            2);

        Assert.Equal(new RectD(-106, -35, 64, 96), bounds);
    }

    [Fact]
    public void IntersectionSplitsCursorAtMonitorBoundary()
    {
        var cursor = new RectD(1_900, 100, 64, 64);
        var leftMonitor = new RectD(0, 0, 1_920, 1_080);
        var rightMonitor = new RectD(1_920, 0, 2_560, 1_440);

        var leftSlice = HotspotMath.Intersect(cursor, leftMonitor);
        var rightSlice = HotspotMath.Intersect(cursor, rightMonitor);

        Assert.Equal(20, leftSlice.Width);
        Assert.Equal(44, rightSlice.Width);
        Assert.Equal(cursor.Width, leftSlice.Width + rightSlice.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScaleIsRejected(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HotspotMath.CalculateTopLeft(
            new PointD(0, 0),
            new PointD(0, 0),
            scale));
    }
}
