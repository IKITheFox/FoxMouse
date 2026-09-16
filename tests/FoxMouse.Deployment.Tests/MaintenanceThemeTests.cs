using System.Drawing;
using FoxMouse.Deployment;

namespace FoxMouse.Deployment.Tests;

public sealed class MaintenanceThemeTests
{
    [Fact]
    public void LayoutMetricsKeepTheWindowResponsiveAndTargetsAccessible()
    {
        Assert.True(MaintenanceLayoutMetrics.MinimumClientWidth <= MaintenanceLayoutMetrics.PreferredClientWidth);
        Assert.True(MaintenanceLayoutMetrics.MinimumClientHeight <= MaintenanceLayoutMetrics.PreferredClientHeight);
        Assert.True(MaintenanceLayoutMetrics.WindowPadding * 2 < MaintenanceLayoutMetrics.MinimumClientWidth);
        Assert.True(MaintenanceLayoutMetrics.ButtonHeight >= 40);
        Assert.True(MaintenanceLayoutMetrics.ButtonMinimumWidth >= 96);
        Assert.True(MaintenanceLayoutMetrics.ControlSpacing > 0);
        Assert.True(MaintenanceLayoutMetrics.SectionSpacing >= MaintenanceLayoutMetrics.ControlSpacing);
        Assert.InRange(
            MaintenanceLayoutMetrics.CornerRadius,
            0,
            MaintenanceLayoutMetrics.ButtonHeight / 2);
    }

    [Theory]
    [InlineData(DeploymentBrandVariant.Light, false)]
    [InlineData(DeploymentBrandVariant.Dark, true)]
    public void StandardPaletteUsesRequestedModeAndPreservesAccent(
        DeploymentBrandVariant variant,
        bool expectedDark)
    {
        Color accent = Color.FromArgb(0, 120, 212);

        MaintenanceThemePalette palette = MaintenanceThemePalette.Resolve(variant, accent);

        Assert.Equal(expectedDark, palette.IsDark);
        Assert.False(palette.IsHighContrast);
        Assert.Equal(accent, palette.Accent);
        Assert.True(ContrastRatio(palette.Text, palette.Window) >= 4.5D);
        Assert.True(ContrastRatio(palette.SecondaryText, palette.Window) >= 4.5D);
        Assert.True(ContrastRatio(palette.Error, palette.ErrorSurface) >= 4.5D);
        Assert.True(ContrastRatio(palette.AccentText, palette.Accent) >= 4.5D);
    }

    [Theory]
    [InlineData(DeploymentBrandVariant.HighContrastBlack)]
    [InlineData(DeploymentBrandVariant.HighContrastWhite)]
    public void HighContrastPaletteUsesSystemColors(DeploymentBrandVariant variant)
    {
        MaintenanceThemePalette palette = MaintenanceThemePalette.Resolve(variant, Color.OrangeRed);

        Assert.True(palette.IsHighContrast);
        Assert.Equal(SystemColors.Window, palette.Window);
        Assert.Equal(SystemColors.Control, palette.Card);
        Assert.Equal(SystemColors.WindowText, palette.Text);
        Assert.Equal(SystemColors.Highlight, palette.Accent);
        Assert.Equal(SystemColors.HighlightText, palette.AccentText);
        Assert.Equal(SystemColors.WindowFrame, palette.Border);
    }

    [Theory]
    [InlineData(250, 210, 20, 0, 0, 0)]
    [InlineData(0, 70, 140, 255, 255, 255)]
    public void AccentTextIsSelectedForReadableContrast(
        int red,
        int green,
        int blue,
        int expectedRed,
        int expectedGreen,
        int expectedBlue)
    {
        Color accent = Color.FromArgb(red, green, blue);

        MaintenanceThemePalette palette = MaintenanceThemePalette.Resolve(
            DeploymentBrandVariant.Light,
            accent);

        Assert.Equal(
            Color.FromArgb(expectedRed, expectedGreen, expectedBlue).ToArgb(),
            palette.AccentText.ToArgb());
        Assert.True(ContrastRatio(palette.AccentText, palette.Accent) >= 4.5D);
    }

    [Theory]
    [InlineData(18, 96, 18)]
    [InlineData(18, 120, 23)]
    [InlineData(18, 144, 27)]
    [InlineData(18, 192, 36)]
    [InlineData(8, 288, 24)]
    public void OwnerDrawMetricsScaleFromLogicalPixels(
        int logicalPixels,
        int dpi,
        int expectedPixels)
    {
        Assert.Equal(expectedPixels, MaintenanceDpi.Scale(logicalPixels, dpi));
    }

    [Fact]
    public void CheckBoxPaintClearsTheEntirePreviousFrame()
    {
        StaTestThread.Run(() =>
        {
            MaintenanceThemePalette palette = MaintenanceThemePalette.Resolve(
                DeploymentBrandVariant.Dark,
                Color.FromArgb(188, 0, 11));
            using MaintenanceCheckBox checkBox = new()
            {
                Text = "卸载时保留个人设置和诊断日志",
                Checked = true,
                Size = new Size(360, 40),
            };
            checkBox.ApplyPalette(palette);
            using Bitmap frame = new(checkBox.Width, checkBox.Height);
            using (Graphics graphics = Graphics.FromImage(frame))
            {
                graphics.Clear(palette.Accent);
            }

            checkBox.DrawToBitmap(frame, checkBox.ClientRectangle);

            Assert.Equal(
                palette.Window.ToArgb(),
                frame.GetPixel(frame.Width - 1, frame.Height - 1).ToArgb());
        });
    }

    [Fact]
    public void ButtonPaintClearsRoundedCornersInsteadOfLeavingAccentPixels()
    {
        StaTestThread.Run(() =>
        {
            MaintenanceThemePalette palette = MaintenanceThemePalette.Resolve(
                DeploymentBrandVariant.Dark,
                Color.FromArgb(188, 0, 11));
            using Panel parent = new()
            {
                BackColor = palette.Window,
                Size = new Size(200, 80),
            };
            using MaintenanceButton button = new()
            {
                Text = "确认卸载",
                VisualStyle = MaintenanceButtonStyle.Accent,
                Location = new Point(20, 20),
                Size = new Size(140, 40),
            };
            parent.Controls.Add(button);
            button.ApplyPalette(palette);
            using Bitmap frame = new(button.Width, button.Height);
            using (Graphics graphics = Graphics.FromImage(frame))
            {
                graphics.Clear(palette.Accent);
            }

            button.DrawToBitmap(frame, button.ClientRectangle);

            Assert.Equal(palette.Window.ToArgb(), frame.GetPixel(0, 0).ToArgb());
        });
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        double brighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
        double darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
        return (brighter + 0.05D) / (darker + 0.05D);
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126D * LinearChannel(color.R)) +
        (0.7152D * LinearChannel(color.G)) +
        (0.0722D * LinearChannel(color.B));

    private static double LinearChannel(byte channel)
    {
        double value = channel / 255D;
        return value <= 0.04045D
            ? value / 12.92D
            : Math.Pow((value + 0.055D) / 1.055D, 2.4D);
    }
}
