using FoxMouse.Platform.Windows.Display;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class DpiUtilitiesTests
{
    [Theory]
    [InlineData(0d, 96u, 0d)]
    [InlineData(96d, 96u, 96d)]
    [InlineData(100d, 120u, 80d)]
    [InlineData(150d, 144u, 100d)]
    [InlineData(384d, 192u, 192d)]
    [InlineData(-192d, 192u, -96d)]
    [InlineData(12.5d, 240u, 5d)]
    [InlineData(288d, 288u, 96d)]
    public void PixelsToDipConvertsWithoutRounding(double pixels, uint dpi, double expected)
    {
        double actual = DpiUtilities.PixelsToDip(pixels, dpi);

        Assert.InRange(actual, expected - 1e-10, expected + 1e-10);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void PixelsToDipClampsInvalidOrTinyDpiToOne(uint dpi)
    {
        Assert.Equal(960d, DpiUtilities.PixelsToDip(10d, dpi));
    }

    [Theory]
    [InlineData(-15360, -8640)]
    [InlineData(-7680, 0)]
    [InlineData(0, -4320)]
    [InlineData(7680, 4320)]
    [InlineData(15360, 8640)]
    public void GetDpiAtEightKAndNegativeVirtualDesktopCoordinatesAlwaysReturnsSaneDpi(int x, int y)
    {
        uint dpi = DpiUtilities.GetDpiAt(new System.Drawing.Point(x, y));

        Assert.InRange(dpi, 48u, 768u);
    }

    [Theory]
    [InlineData(32d, 96u, 32d)]
    [InlineData(40d, 120u, 32d)]
    [InlineData(48d, 144u, 32d)]
    [InlineData(64d, 192u, 32d)]
    [InlineData(96d, 288u, 32d)]
    public void MixedDpiPhysicalCursorSizesResolveToSameDipSize(double pixels, uint dpi, double expectedDip)
    {
        Assert.Equal(expectedDip, DpiUtilities.PixelsToDip(pixels, dpi), precision: 10);
    }
}
