namespace FoxMouse.Core;

public readonly record struct PointD(double X, double Y);

public readonly record struct RectD(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public static class HotspotMath
{
    public static PointD CalculateTopLeft(
        PointD cursorHotspotScreenPosition,
        PointD sourceHotspot,
        double scale)
    {
        ValidateScale(scale);
        ValidateFinite(cursorHotspotScreenPosition, nameof(cursorHotspotScreenPosition));
        ValidateFinite(sourceHotspot, nameof(sourceHotspot));

        return new PointD(
            cursorHotspotScreenPosition.X - (sourceHotspot.X * scale),
            cursorHotspotScreenPosition.Y - (sourceHotspot.Y * scale));
    }

    public static RectD CalculateBounds(
        PointD cursorHotspotScreenPosition,
        PointD sourceHotspot,
        double sourceWidth,
        double sourceHeight,
        double scale)
    {
        if (!double.IsFinite(sourceWidth) || sourceWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        }

        if (!double.IsFinite(sourceHeight) || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        }

        var topLeft = CalculateTopLeft(cursorHotspotScreenPosition, sourceHotspot, scale);
        return new RectD(topLeft.X, topLeft.Y, sourceWidth * scale, sourceHeight * scale);
    }

    public static RectD Intersect(RectD first, RectD second)
    {
        ValidateRect(first, nameof(first));
        ValidateRect(second, nameof(second));

        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);

        return right <= left || bottom <= top
            ? new RectD(left, top, 0, 0)
            : new RectD(left, top, right - left, bottom - top);
    }

    public static PointD ReconstructHotspot(RectD bounds, PointD sourceHotspot, double scale)
    {
        ValidateRect(bounds, nameof(bounds));
        ValidateFinite(sourceHotspot, nameof(sourceHotspot));
        ValidateScale(scale);

        return new PointD(
            bounds.Left + (sourceHotspot.X * scale),
            bounds.Top + (sourceHotspot.Y * scale));
    }

    private static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
    }

    private static void ValidateFinite(PointD point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateRect(RectD rectangle, string parameterName)
    {
        if (!double.IsFinite(rectangle.Left)
            || !double.IsFinite(rectangle.Top)
            || !double.IsFinite(rectangle.Width)
            || !double.IsFinite(rectangle.Height)
            || rectangle.Width < 0
            || rectangle.Height < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
