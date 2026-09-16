using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Rendering;

/// <summary>
/// Reusable top-down 32-bit DIB selected into one memory DC. Keeping these
/// native objects alive removes the per-frame HBITMAP/DC churn that otherwise
/// becomes visible on high polling-rate mice and large cursor rasters.
/// </summary>
internal sealed class LayeredBitmapSurface : IDisposable
{
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const int MaximumDimension = 2_048;

    private nint _memoryDc;
    private nint _bitmapHandle;
    private nint _previousObject;
    private Bitmap? _bitmap;
    private Graphics? _graphics;
    private bool _disposed;

    internal nint DeviceContext => _memoryDc;

    internal Size Size { get; private set; }

    internal Graphics BeginDraw(int requiredWidth, int requiredHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(requiredWidth, requiredHeight);
        Graphics graphics = _graphics!;
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.Clear(Color.Transparent);
        return graphics;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseSurface();
        if (_memoryDc != nint.Zero)
        {
            _ = NativeMethods.DeleteDC(_memoryDc);
            _memoryDc = nint.Zero;
        }
    }

    private void EnsureCapacity(int requiredWidth, int requiredHeight)
    {
        requiredWidth = Math.Clamp(requiredWidth, 1, MaximumDimension);
        requiredHeight = Math.Clamp(requiredHeight, 1, MaximumDimension);
        if (_bitmap is not null && Size.Width >= requiredWidth && Size.Height >= requiredHeight)
        {
            return;
        }

        int width = GrowDimension(requiredWidth);
        int height = GrowDimension(requiredHeight);
        if (_memoryDc == nint.Zero)
        {
            _memoryDc = NativeMethods.CreateCompatibleDC(nint.Zero);
            if (_memoryDc == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the overlay memory DC.");
            }
        }

        ReleaseSurface();
        BitmapInfo bitmapInfo = new()
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
                SizeImage = checked((uint)(width * height * 4)),
            },
        };
        _bitmapHandle = CreateDIBSection(
            nint.Zero,
            ref bitmapInfo,
            DibRgbColors,
            out nint bits,
            nint.Zero,
            0);
        if (_bitmapHandle == nint.Zero || bits == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _bitmapHandle = nint.Zero;
            throw new Win32Exception(error, "Could not create the overlay DIB surface.");
        }

        _previousObject = NativeMethods.SelectObject(_memoryDc, _bitmapHandle);
        if (_previousObject == nint.Zero || _previousObject == new nint(-1))
        {
            int error = Marshal.GetLastWin32Error();
            _ = NativeMethods.DeleteObject(_bitmapHandle);
            _bitmapHandle = nint.Zero;
            _previousObject = nint.Zero;
            throw new Win32Exception(error, "Could not select the overlay DIB surface.");
        }

        _bitmap = new Bitmap(width, height, checked(width * 4), PixelFormat.Format32bppPArgb, bits);
        _graphics = Graphics.FromImage(_bitmap);
        Size = new Size(width, height);
    }

    private void ReleaseSurface()
    {
        _graphics?.Dispose();
        _graphics = null;
        _bitmap?.Dispose();
        _bitmap = null;

        if (_bitmapHandle != nint.Zero)
        {
            if (_memoryDc != nint.Zero && _previousObject != nint.Zero)
            {
                _ = NativeMethods.SelectObject(_memoryDc, _previousObject);
            }

            _ = NativeMethods.DeleteObject(_bitmapHandle);
            _bitmapHandle = nint.Zero;
            _previousObject = nint.Zero;
        }

        Size = Size.Empty;
    }

    private static int GrowDimension(int required)
    {
        int rounded = ((required + 63) / 64) * 64;
        return Math.Clamp(rounded, 64, MaximumDimension);
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(
        nint dc,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }
}
