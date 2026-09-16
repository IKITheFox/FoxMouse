using System.Drawing;

namespace FoxMouse.Platform.Windows.Cursor;

public enum CursorRenderQuality
{
    NativeRaster,
    SystemScaled,
    ResourceMatched,
    UpscaledFallback,
}

public sealed class CursorImage : IDisposable
{
    public CursorImage(nint handleKey, Bitmap bitmap, Point hotspot, bool supportsReplacement)
        : this(
            handleKey,
            bitmap,
            bitmap.Size,
            hotspot,
            supportsReplacement,
            CursorRenderQuality.NativeRaster)
    {
    }

    public CursorImage(
        nint handleKey,
        Bitmap bitmap,
        Size displaySize,
        Point hotspot,
        bool supportsReplacement,
        CursorRenderQuality renderQuality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(displaySize.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(displaySize.Height, 0);
        HandleKey = handleKey;
        Bitmap = bitmap;
        DisplaySize = displaySize;
        Hotspot = hotspot;
        SupportsReplacement = supportsReplacement;
        RenderQuality = renderQuality;
    }

    public nint HandleKey { get; }

    public Bitmap Bitmap { get; }

    /// <summary>
    /// Physical size of the native cursor on the active monitor. Bitmap can
    /// be larger because FoxMouse resolves a higher-resolution resource for
    /// the enlarged result.
    /// </summary>
    public Size DisplaySize { get; }

    public Point Hotspot { get; }

    public bool SupportsReplacement { get; }

    public CursorRenderQuality RenderQuality { get; }

    public double ResolutionScale => Math.Min(
        Bitmap.Width / (double)DisplaySize.Width,
        Bitmap.Height / (double)DisplaySize.Height);

    public void Dispose() => Bitmap.Dispose();
}
