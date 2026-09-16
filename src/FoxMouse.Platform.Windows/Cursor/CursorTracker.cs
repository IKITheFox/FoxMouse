using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Cursor;

public sealed class CursorTracker : IDisposable
{
    private const int MaximumRasterDimension = 2_048;
    private const uint ImageCursor = 2;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrCopyFromResource = 0x00004000;

    private CursorImage? _cachedImage;
    private bool _disposed;

    public bool TryObserve(
        out CursorObservation observation,
        bool allowCachedWhenSystemHidden = false,
        double requestedMaximumScale = 1.0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        NativeMethods.CursorInfo info = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.CursorInfo>(),
        };

        if (!NativeMethods.GetCursorInfo(ref info))
        {
            observation = default;
            return false;
        }

        bool visible = (info.Flags & NativeMethods.CursorShowing) != 0;
        Point position = new(info.ScreenPosition.X, info.ScreenPosition.Y);
        if (info.Cursor == nint.Zero)
        {
            if (allowCachedWhenSystemHidden && _cachedImage is not null)
            {
                observation = new CursorObservation(position, true, _cachedImage);
                return true;
            }

            observation = new CursorObservation(position, false, null);
            return true;
        }

        if (!visible && !allowCachedWhenSystemHidden)
        {
            observation = new CursorObservation(position, false, null);
            return true;
        }

        double safeMaximumScale = double.IsFinite(requestedMaximumScale)
            ? Math.Clamp(requestedMaximumScale, 1.0, 8.0)
            : 1.0;
        if (_cachedImage is null ||
            _cachedImage.HandleKey != info.Cursor ||
            _cachedImage.ResolutionScale + 0.01 < safeMaximumScale)
        {
            CursorImage? replacement = Capture(info.Cursor, safeMaximumScale);
            if (replacement is null)
            {
                observation = new CursorObservation(position, true, null);
                return true;
            }

            if (!replacement.SupportsReplacement &&
                _cachedImage is { SupportsReplacement: true })
            {
                // Keep the last proven raster alive for the engine's bounded
                // transition grace period. Publishing the unsupported image
                // would dispose that fallback before the native cursor could
                // be restored safely.
                replacement.Dispose();
                observation = new CursorObservation(position, true, null);
                return true;
            }

            _cachedImage?.Dispose();
            _cachedImage = replacement;
        }

        observation = new CursorObservation(position, visible || allowCachedWhenSystemHidden, _cachedImage);
        return true;
    }

    public void Invalidate()
    {
        _cachedImage?.Dispose();
        _cachedImage = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Invalidate();
        GC.SuppressFinalize(this);
    }

    private static CursorImage? Capture(nint cursorHandle, double requestedMaximumScale)
    {
        nint ownedHandle = NativeMethods.CopyIcon(cursorHandle);
        if (ownedHandle == nint.Zero)
        {
            return null;
        }

        NativeMethods.IconInfo iconInfo = default;
        bool hasInfo = false;
        try
        {
            hasInfo = NativeMethods.GetIconInfo(ownedHandle, out iconInfo);
            if (!hasInfo)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            bool nativeMonochrome = iconInfo.ColorBitmap == nint.Zero;
            Size displaySize = ReadBitmapSize(iconInfo, nativeMonochrome);
            if (displaySize.Width <= 0 ||
                displaySize.Height <= 0 ||
                displaySize.Width > MaximumRasterDimension ||
                displaySize.Height > MaximumRasterDimension)
            {
                return null;
            }

            int targetWidth = Math.Clamp(
                (int)Math.Ceiling(displaySize.Width * requestedMaximumScale),
                displaySize.Width,
                MaximumRasterDimension);
            int targetHeight = Math.Clamp(
                (int)Math.Ceiling(displaySize.Height * requestedMaximumScale),
                displaySize.Height,
                MaximumRasterDimension);

            nint renderHandle = nint.Zero;
            CursorRenderQuality renderQuality = CursorRenderQuality.NativeRaster;
            if (targetWidth > displaySize.Width || targetHeight > displaySize.Height)
            {
                renderHandle = TryLoadCursorFileResource(cursorHandle, targetWidth, targetHeight);
                if (renderHandle != nint.Zero)
                {
                    renderQuality = CursorRenderQuality.ResourceMatched;
                }
                else
                {
                    renderHandle = CopyImage(
                        cursorHandle,
                        ImageCursor,
                        targetWidth,
                        targetHeight,
                        LrCopyFromResource);
                    renderQuality = renderHandle == nint.Zero
                        ? CursorRenderQuality.UpscaledFallback
                        : CursorRenderQuality.SystemScaled;
                }
            }

            nint effectiveHandle = renderHandle != nint.Zero ? renderHandle : ownedHandle;
            Bitmap? bitmap = null;
            bool renderedMonochrome = nativeMonochrome;
            try
            {
                if (!TryRasterize(effectiveHandle, out bitmap, out renderedMonochrome))
                {
                    return null;
                }
            }
            finally
            {
                if (renderHandle != nint.Zero)
                {
                    _ = DestroyCursor(renderHandle);
                }
            }

            Point hotspot = new((int)iconInfo.HotspotX, (int)iconInfo.HotspotY);
            bool hotspotValid = hotspot.X >= 0 && hotspot.Y >= 0 &&
                                hotspot.X < displaySize.Width && hotspot.Y < displaySize.Height;
            bool supportsReplacement = !renderedMonochrome &&
                                       hotspotValid &&
                                       HasNonTransparentPixels(bitmap!);
            return new CursorImage(
                cursorHandle,
                bitmap!,
                displaySize,
                hotspot,
                supportsReplacement,
                renderQuality);
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (hasInfo)
            {
                if (iconInfo.ColorBitmap != nint.Zero)
                {
                    _ = NativeMethods.DeleteObject(iconInfo.ColorBitmap);
                }

                if (iconInfo.MaskBitmap != nint.Zero)
                {
                    _ = NativeMethods.DeleteObject(iconInfo.MaskBitmap);
                }
            }

            _ = NativeMethods.DestroyIcon(ownedHandle);
        }
    }

    private static bool TryRasterize(nint cursorHandle, out Bitmap? bitmap, out bool monochrome)
    {
        bitmap = null;
        monochrome = false;
        if (!NativeMethods.GetIconInfo(cursorHandle, out NativeMethods.IconInfo info))
        {
            return false;
        }

        try
        {
            monochrome = info.ColorBitmap == nint.Zero;
            Size bitmapSize = ReadBitmapSize(info, monochrome);
            if (bitmapSize.Width <= 0 ||
                bitmapSize.Height <= 0 ||
                bitmapSize.Width > MaximumRasterDimension ||
                bitmapSize.Height > MaximumRasterDimension)
            {
                return false;
            }

            using Icon borrowedIcon = Icon.FromHandle(cursorHandle);
            using Icon icon = (Icon)borrowedIcon.Clone();
            bitmap = new Bitmap(bitmapSize.Width, bitmapSize.Height, PixelFormat.Format32bppPArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.DrawIcon(icon, new Rectangle(Point.Empty, bitmapSize));
            return true;
        }
        catch (ArgumentException)
        {
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
        finally
        {
            if (info.ColorBitmap != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(info.ColorBitmap);
            }

            if (info.MaskBitmap != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(info.MaskBitmap);
            }
        }
    }

    private static nint TryLoadCursorFileResource(nint cursorHandle, int width, int height)
    {
        IconInfoEx info = new()
        {
            Size = (uint)Marshal.SizeOf<IconInfoEx>(),
            ModuleName = string.Empty,
        };
        if (!GetIconInfoEx(cursorHandle, ref info))
        {
            return nint.Zero;
        }

        try
        {
            string path = info.ModuleName ?? string.Empty;
            if (path.Length == 0 ||
                !File.Exists(path) ||
                (!path.EndsWith(".cur", StringComparison.OrdinalIgnoreCase) &&
                 !path.EndsWith(".ani", StringComparison.OrdinalIgnoreCase)))
            {
                return nint.Zero;
            }

            return LoadImage(nint.Zero, path, ImageCursor, width, height, LrLoadFromFile);
        }
        finally
        {
            if (info.ColorBitmap != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(info.ColorBitmap);
            }

            if (info.MaskBitmap != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(info.MaskBitmap);
            }
        }
    }

    private static Size ReadBitmapSize(NativeMethods.IconInfo info, bool monochrome)
    {
        nint bitmapHandle = info.ColorBitmap != nint.Zero ? info.ColorBitmap : info.MaskBitmap;
        if (bitmapHandle == nint.Zero ||
            NativeMethods.GetObject(
                bitmapHandle,
                Marshal.SizeOf<NativeMethods.BitmapInfo>(),
                out NativeMethods.BitmapInfo bitmap) == 0)
        {
            return Size.Empty;
        }

        int height = monochrome ? Math.Abs(bitmap.Height) / 2 : Math.Abs(bitmap.Height);
        return new Size(Math.Abs(bitmap.Width), height);
    }

    internal static unsafe bool HasNonTransparentPixels(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Rectangle bounds = new(Point.Empty, bitmap.Size);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < data.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + (y * data.Stride);
                for (int x = 0; x < data.Width; x++)
                {
                    if (row[(x * 4) + 3] != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [DllImport("user32.dll", EntryPoint = "CopyImage", SetLastError = true)]
    private static extern nint CopyImage(
        nint image,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetIconInfoExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfoEx(nint iconOrCursor, ref IconInfoEx iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint cursor);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconInfoEx
    {
        internal uint Size;
        internal int IsIcon;
        internal uint HotspotX;
        internal uint HotspotY;
        internal nint MaskBitmap;
        internal nint ColorBitmap;
        internal ushort ResourceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ModuleName;
    }

}
