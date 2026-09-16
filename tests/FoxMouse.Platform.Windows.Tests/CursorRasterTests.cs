using System.Drawing;
using System.Drawing.Imaging;
using FoxMouse.Platform.Windows.Cursor;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class CursorRasterTests
{
    [Theory]
    [InlineData(32, 32, 32, 32, 1.0)]
    [InlineData(256, 256, 32, 32, 8.0)]
    [InlineData(512, 256, 64, 64, 4.0)]
    [InlineData(768, 512, 96, 64, 8.0)]
    public void CursorImageExposesDisplayResolutionAndConservativeResolutionScale(
        int bitmapWidth,
        int bitmapHeight,
        int displayWidth,
        int displayHeight,
        double expectedResolutionScale)
    {
        using Bitmap bitmap = new(bitmapWidth, bitmapHeight, PixelFormat.Format32bppPArgb);
        using CursorImage image = new(
            new nint(42),
            bitmap,
            new Size(displayWidth, displayHeight),
            new Point(displayWidth / 4, displayHeight / 4),
            supportsReplacement: true,
            CursorRenderQuality.ResourceMatched);

        Assert.Equal(new Size(displayWidth, displayHeight), image.DisplaySize);
        Assert.Equal(expectedResolutionScale, image.ResolutionScale, precision: 10);
        Assert.Equal(CursorRenderQuality.ResourceMatched, image.RenderQuality);
        Assert.True(image.SupportsReplacement);
    }

    [Theory]
    [InlineData(CursorRenderQuality.NativeRaster)]
    [InlineData(CursorRenderQuality.SystemScaled)]
    [InlineData(CursorRenderQuality.ResourceMatched)]
    [InlineData(CursorRenderQuality.UpscaledFallback)]
    public void CursorImagePreservesEveryRenderQualityTier(CursorRenderQuality quality)
    {
        using Bitmap bitmap = new(64, 64, PixelFormat.Format32bppPArgb);
        using CursorImage image = new(
            nint.Zero,
            bitmap,
            new Size(32, 32),
            Point.Empty,
            supportsReplacement: true,
            quality);

        Assert.Equal(quality, image.RenderQuality);
        Assert.Equal(2d, image.ResolutionScale);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(32, 0)]
    [InlineData(-1, 32)]
    [InlineData(32, -1)]
    public void CursorImageRejectsInvalidPhysicalDisplaySize(int width, int height)
    {
        using Bitmap bitmap = new(32, 32, PixelFormat.Format32bppPArgb);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CursorImage(
            nint.Zero,
            bitmap,
            new Size(width, height),
            Point.Empty,
            supportsReplacement: true,
            CursorRenderQuality.NativeRaster));
    }

    [Fact]
    public void FullyTransparentRasterHasNoReplacementPixels()
    {
        using Bitmap bitmap = new(16, 16, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
        }

        Assert.False(CursorTracker.HasNonTransparentPixels(bitmap));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(255)]
    public void AnyNonTransparentPixelMakesRasterUsable(int alpha)
    {
        using Bitmap bitmap = new(16, 16, PixelFormat.Format32bppPArgb);
        bitmap.SetPixel(9, 7, Color.FromArgb(alpha, 255, 128, 64));

        Assert.True(CursorTracker.HasNonTransparentPixels(bitmap));
    }

}
