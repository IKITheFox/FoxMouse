using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using FoxMouse.Platform.Windows.Rendering;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class LocatorRingRasterTests
{
    [Theory]
    [InlineData(1d, 0d)]
    [InlineData(1d, 1d)]
    [InlineData(1.5d, 0.5d)]
    [InlineData(2d, 1d)]
    [InlineData(3d, 1d)]
    [InlineData(4d, 1d)]
    public void TransparentRingHasNoWhiteEdgeAtDifferentDpiScales(double dpiScale, double progress)
    {
        int diameter = (int)Math.Round((48 + 36 * progress) * dpiScale);
        int padding = (int)Math.Round(8 * dpiScale);
        int size = diameter + padding * 2;
        using Bitmap bitmap = new(size, size, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(Color.Transparent);
            LayeredCursorOverlay.DrawLocatorRing(graphics, diameter, padding, (float)((4 + 2 * progress) * dpiScale));
        }
        int orangePixels = 0;
        int translucentPixels = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            Color color = bitmap.GetPixel(x, y);
            if (color.A == 0) continue;
            if (color.A < 240) translucentPixels++;
            // Ignore near-transparent rounding noise; a white outline would
            // have comparable RGB channels and fail this orange hue invariant.
            if (color.A >= 32)
            {
                Assert.True(color.R > color.G * 2 && color.G > color.B,
                    $"Unexpected edge color at {x},{y}: {color}");
                orangePixels++;
            }
        }
        Assert.True(orangePixels > 100);
        Assert.True(translucentPixels > 0);
        Assert.Equal(0, bitmap.GetPixel(size / 2, size / 2).A);
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
    }
}
