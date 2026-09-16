using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FoxMouse.App.UI;

internal enum TrayMenuGlyph
{
    Preview,
    Settings,
    Folder,
    About,
    Exit,
}

internal enum BrandIconVariant
{
    Light,
    Dark,
    HighContrastBlack,
    HighContrastWhite,
}

internal static class FoxMouseIconFactory
{
    private const string ResourcePrefix = "FoxMouse.Branding.";

    internal static BrandIconVariant ResolveBrandIconVariant(SemanticColors colors)
    {
        if (colors.IsHighContrast)
        {
            return colors.Text.GetBrightness() >= 0.5F
                ? BrandIconVariant.HighContrastWhite
                : BrandIconVariant.HighContrastBlack;
        }

        return colors.IsDark ? BrandIconVariant.Dark : BrandIconVariant.Light;
    }

    internal static Icon CreateApplicationIcon(SemanticColors colors) =>
        CreateApplicationIcon(ResolveBrandIconVariant(colors));

    internal static Icon CreateApplicationIcon(BrandIconVariant variant)
    {
        try
        {
            using Stream? stream = typeof(FoxMouseIconFactory).Assembly.GetManifestResourceStream(
                ResourcePrefix + GetIconResourceName(variant));
            if (stream is not null)
            {
                using Icon source = new(stream);
                return (Icon)source.Clone();
            }

            string executablePath = Application.ExecutablePath;
            if (File.Exists(executablePath))
            {
                using Icon? associatedIcon = Icon.ExtractAssociatedIcon(executablePath);
                if (associatedIcon is not null)
                {
                    return (Icon)associatedIcon.Clone();
                }
            }
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException or IOException)
        {
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    internal static Bitmap CreateBrandMark(SemanticColors colors)
    {
        BrandIconVariant variant = ResolveBrandIconVariant(colors);
        using Stream? stream = typeof(FoxMouseIconFactory).Assembly.GetManifestResourceStream(
            ResourcePrefix + GetMarkResourceName(variant));
        if (stream is null)
        {
            using Icon icon = CreateApplicationIcon(variant);
            return icon.ToBitmap();
        }

        using Bitmap source = new(stream);
        return new Bitmap(source);
    }

    internal static Bitmap CreateMenuIcon(TrayMenuGlyph glyph, Color color, int size = 20)
    {
        Bitmap bitmap = new(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        float scale = size / 20F;
        using Pen pen = new(color, Math.Max(1.4F, 1.6F * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        switch (glyph)
        {
            case TrayMenuGlyph.Preview:
                graphics.DrawEllipse(pen, 3 * scale, 3 * scale, 10 * scale, 10 * scale);
                graphics.DrawLine(pen, 12 * scale, 12 * scale, 17 * scale, 17 * scale);
                break;
            case TrayMenuGlyph.Settings:
                graphics.DrawEllipse(pen, 7 * scale, 7 * scale, 6 * scale, 6 * scale);
                graphics.DrawEllipse(pen, 3 * scale, 3 * scale, 14 * scale, 14 * scale);
                graphics.DrawLine(pen, 10 * scale, 1.5F * scale, 10 * scale, 4 * scale);
                graphics.DrawLine(pen, 10 * scale, 16 * scale, 10 * scale, 18.5F * scale);
                graphics.DrawLine(pen, 1.5F * scale, 10 * scale, 4 * scale, 10 * scale);
                graphics.DrawLine(pen, 16 * scale, 10 * scale, 18.5F * scale, 10 * scale);
                break;
            case TrayMenuGlyph.Folder:
                using (GraphicsPath folder = CreateFolderPath(scale))
                {
                    graphics.DrawPath(pen, folder);
                }
                break;
            case TrayMenuGlyph.About:
                graphics.DrawEllipse(pen, 3 * scale, 3 * scale, 14 * scale, 14 * scale);
                graphics.DrawLine(pen, 10 * scale, 9 * scale, 10 * scale, 14 * scale);
                graphics.DrawEllipse(pen, 9.6F * scale, 6 * scale, 0.8F * scale, 0.8F * scale);
                break;
            case TrayMenuGlyph.Exit:
                graphics.DrawArc(pen, 3 * scale, 4 * scale, 14 * scale, 14 * scale, -45, 270);
                graphics.DrawLine(pen, 10 * scale, 1.5F * scale, 10 * scale, 10 * scale);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(glyph), glyph, null);
        }

        return bitmap;
    }

    private static GraphicsPath CreateFolderPath(float scale)
    {
        GraphicsPath path = new();
        path.AddLines(
        [
            new PointF(2 * scale, 6 * scale),
            new PointF(2 * scale, 16 * scale),
            new PointF(18 * scale, 16 * scale),
            new PointF(18 * scale, 7 * scale),
            new PointF(10 * scale, 7 * scale),
            new PointF(8 * scale, 4 * scale),
            new PointF(2 * scale, 4 * scale),
            new PointF(2 * scale, 6 * scale),
        ]);
        return path;
    }

    private static string GetIconResourceName(BrandIconVariant variant) => variant switch
    {
        BrandIconVariant.Dark => "FoxMouse.Dark.ico",
        BrandIconVariant.HighContrastBlack => "FoxMouse.HighContrast.Black.ico",
        BrandIconVariant.HighContrastWhite => "FoxMouse.HighContrast.White.ico",
        _ => "FoxMouse.ico",
    };

    private static string GetMarkResourceName(BrandIconVariant variant) => variant switch
    {
        BrandIconVariant.Dark => "FoxMouse.Mark.Dark.png",
        BrandIconVariant.HighContrastBlack => "FoxMouse.Mark.HighContrast.Black.png",
        BrandIconVariant.HighContrastWhite => "FoxMouse.Mark.HighContrast.White.png",
        _ => "FoxMouse.Mark.Light.png",
    };

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);
}
