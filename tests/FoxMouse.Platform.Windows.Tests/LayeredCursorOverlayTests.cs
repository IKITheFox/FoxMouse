using System.Runtime.InteropServices;
using FoxMouse.Platform.Windows.Cursor;
using FoxMouse.Platform.Windows.Rendering;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class LayeredCursorOverlayTests
{
    private const int GwlExStyle = -20;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExTopMost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndNoTopMost = new(-2);
    private static readonly nint HtTransparent = new(-1);
    private static readonly nint MaNoActivate = new(3);

    [WindowsDesktopFact]
    public void ConstructorCreatesHiddenNonActivatingClickThroughWindow()
    {
        StaTestThread.Run(() =>
        {
            using LayeredCursorOverlay overlay = new();

            Assert.NotEqual(nint.Zero, overlay.Handle);
            Assert.False(overlay.IsVisible);
            Assert.False(overlay.ShowInTaskbar);
            Assert.True(overlay.TopMost);
            Assert.Equal(FormBorderStyle.None, overlay.FormBorderStyle);
            Assert.Equal(AutoScaleMode.None, overlay.AutoScaleMode);

            long styles = GetExtendedStyles(overlay.Handle);
            AssertStyle(styles, WsExLayered);
            AssertStyle(styles, WsExTransparent);
            AssertStyle(styles, WsExToolWindow);
            AssertStyle(styles, WsExNoActivate);
            AssertStyle(styles, WsExTopMost);

            Assert.Equal(HtTransparent, SendMessage(overlay.Handle, WmNcHitTest, nint.Zero, nint.Zero));
            Assert.Equal(MaNoActivate, SendMessage(overlay.Handle, WmMouseActivate, nint.Zero, nint.Zero));
        });
    }

    [WindowsDesktopFact]
    public void InvisibleCursorPathAndHideAreIdempotentAndNeverShowOverlay()
    {
        StaTestThread.Run(() =>
        {
            using LayeredCursorOverlay overlay = new();
            CursorObservation invisible = new(new System.Drawing.Point(10, 20), false, null);

            overlay.ShowCursor(invisible, scale: 4d);
            overlay.HideOverlay();
            overlay.HideOverlay();

            Assert.False(overlay.IsVisible);
        });
    }

    [WindowsDesktopFact]
    public void RenderingReassertsTopMostAfterExternalDemotionAndPreservesRecovery()
    {
        StaTestThread.Run(() =>
        {
            using LayeredCursorOverlay overlay = new();
            System.Drawing.Point position = System.Windows.Forms.Cursor.Position;

            overlay.ShowLocator(position, progress: 0.5d);
            Assert.True(overlay.IsVisible);
            AssertStyle(GetExtendedStyles(overlay.Handle), WsExTopMost);

            overlay.HideOverlay();
            Assert.False(overlay.IsVisible);
            Assert.True(SetWindowPos(
                overlay.Handle,
                HwndNoTopMost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate));
            Assert.Equal(0, GetExtendedStyles(overlay.Handle) & WsExTopMost);

            overlay.ShowLocator(position, progress: 1d);

            Assert.True(overlay.IsVisible);
            long restoredStyles = GetExtendedStyles(overlay.Handle);
            AssertStyle(restoredStyles, WsExTopMost);
            AssertStyle(restoredStyles, WsExNoActivate);
            AssertStyle(restoredStyles, WsExTransparent);

            overlay.HideOverlay();
            Assert.False(overlay.IsVisible);
        });
    }

    [Fact]
    public void TwoKSurfaceCapUsesIndependentRenderedScalesForHotspot()
    {
        (System.Drawing.Size size, System.Drawing.Point location) =
            LayeredCursorOverlay.CalculateCursorGeometry(
                new System.Drawing.Size(512, 64),
                new System.Drawing.Point(256, 32),
                new System.Drawing.Point(1_000, 500),
                requestedScale: 6d);

        Assert.Equal(new System.Drawing.Size(2_048, 384), size);
        Assert.Equal(new System.Drawing.Point(-24, 308), location);
    }

    [Theory]
    [InlineData(-7680, -4320, 32, 32, 0, 0, 8.0, 256, 256, -7680, -4320)]
    [InlineData(7680, 4320, 64, 64, 32, 32, 8.0, 512, 512, 7424, 4064)]
    [InlineData(-3840, 1080, 256, 512, 255, 511, 8.0, 2048, 2048, -5880, -964)]
    [InlineData(15360, -4320, 512, 128, 256, 64, 8.0, 2048, 1024, 14336, -4832)]
    public void GeometrySupportsEightKDesktopEdgesAndNegativeMonitorCoordinates(
        int pointerX,
        int pointerY,
        int displayWidth,
        int displayHeight,
        int hotspotX,
        int hotspotY,
        double scale,
        int expectedWidth,
        int expectedHeight,
        int expectedX,
        int expectedY)
    {
        (System.Drawing.Size size, System.Drawing.Point location) =
            LayeredCursorOverlay.CalculateCursorGeometry(
                new System.Drawing.Size(displayWidth, displayHeight),
                new System.Drawing.Point(hotspotX, hotspotY),
                new System.Drawing.Point(pointerX, pointerY),
                scale);

        Assert.Equal(new System.Drawing.Size(expectedWidth, expectedHeight), size);
        Assert.Equal(new System.Drawing.Point(expectedX, expectedY), location);
        Assert.InRange(size.Width, 1, 2_048);
        Assert.InRange(size.Height, 1, 2_048);
    }

    [Theory]
    [InlineData(96u, 32, 4.0, 128)]
    [InlineData(120u, 40, 4.0, 160)]
    [InlineData(144u, 48, 4.0, 192)]
    [InlineData(192u, 64, 4.0, 256)]
    [InlineData(288u, 96, 4.0, 384)]
    public void MixedDpiDisplaySizesProduceEquivalentDipGeometry(
        uint dpi,
        int physicalDisplaySize,
        double scale,
        int expectedPhysicalSize)
    {
        double sourceDipSize = physicalDisplaySize * 96d / dpi;
        Assert.Equal(32d, sourceDipSize, precision: 10);

        (System.Drawing.Size size, System.Drawing.Point location) =
            LayeredCursorOverlay.CalculateCursorGeometry(
                new System.Drawing.Size(physicalDisplaySize, physicalDisplaySize),
                new System.Drawing.Point(physicalDisplaySize / 4, physicalDisplaySize / 4),
                new System.Drawing.Point(-8_000, 4_320),
                scale);

        Assert.Equal(new System.Drawing.Size(expectedPhysicalSize, expectedPhysicalSize), size);
        Assert.Equal(
            new System.Drawing.Point(-8_000 - (expectedPhysicalSize / 4), 4_320 - (expectedPhysicalSize / 4)),
            location);
    }

    [WindowsDesktopFact]
    public void ReusableSurfaceKeepsDeviceContextAndCapacityForRepeatedFrames()
    {
        StaTestThread.Run(() =>
        {
            using LayeredBitmapSurface surface = new();

            _ = surface.BeginDraw(257, 513);
            nint deviceContext = surface.DeviceContext;
            System.Drawing.Size initialCapacity = surface.Size;

            Assert.NotEqual(nint.Zero, deviceContext);
            Assert.Equal(new System.Drawing.Size(320, 576), initialCapacity);

            for (int frame = 0; frame < 1_000; frame++)
            {
                int width = 64 + (frame % 193);
                int height = 64 + (frame % 385);
                _ = surface.BeginDraw(width, height);
                Assert.Equal(deviceContext, surface.DeviceContext);
                Assert.Equal(initialCapacity, surface.Size);
            }
        });
    }

    [Theory]
    [InlineData(-3840, -2160, 96, 32, 32, 4, 6, 2.0, -3848, -2172)]
    [InlineData(-1920, 0, 144, 48, 64, 12, 18, 3.0, -1956, -54)]
    [InlineData(0, -2160, 192, 64, 64, 32, 32, 4.0, -128, -2288)]
    [InlineData(7680, 4320, 288, 96, 128, 20, 30, 6.0, 7560, 4140)]
    public void GeometryPreservesHotspotAcrossNegativeCoordinatesAndDpiTargets(
        int pointerX,
        int pointerY,
        uint dpi,
        int sourceWidth,
        int sourceHeight,
        int hotspotX,
        int hotspotY,
        double scale,
        int expectedX,
        int expectedY)
    {
        // DPI is part of the matrix contract even though the overlay receives
        // an already-rasterized cursor at the target monitor's physical size.
        Assert.InRange(dpi, 96u, 288u);
        (System.Drawing.Size _, System.Drawing.Point location) =
            LayeredCursorOverlay.CalculateCursorGeometry(
                new System.Drawing.Size(sourceWidth, sourceHeight),
                new System.Drawing.Point(hotspotX, hotspotY),
                new System.Drawing.Point(pointerX, pointerY),
                scale);

        Assert.Equal(new System.Drawing.Point(expectedX, expectedY), location);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteScaleFallsBackToOne(double scale)
    {
        (System.Drawing.Size size, System.Drawing.Point location) =
            LayeredCursorOverlay.CalculateCursorGeometry(
                new System.Drawing.Size(32, 48),
                new System.Drawing.Point(4, 6),
                new System.Drawing.Point(100, 200),
                scale);

        Assert.Equal(new System.Drawing.Size(32, 48), size);
        Assert.Equal(new System.Drawing.Point(96, 194), location);
    }

    private static void AssertStyle(long styles, long expectedStyle)
    {
        Assert.Equal(expectedStyle, styles & expectedStyle);
    }

    private static long GetExtendedStyles(nint window)
    {
        nint value = Environment.Is64BitProcess
            ? GetWindowLongPtr(window, GwlExStyle)
            : new nint(GetWindowLong(window, GwlExStyle));
        return value.ToInt64();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
