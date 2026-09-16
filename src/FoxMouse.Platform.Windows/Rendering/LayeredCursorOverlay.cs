using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using FoxMouse.Platform.Windows.Cursor;
using FoxMouse.Platform.Windows.Display;
using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Rendering;

public sealed class LayeredCursorOverlay : Form, IOverlayController
{
    private const int MaximumSurfaceDimension = 2_048;

    private readonly LayeredBitmapSurface _surface = new();
    private bool _disposed;

    public LayeredCursorOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(1, 1);
        _ = Handle;
    }

    public bool IsVisible { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WsExLayered |
                                  NativeMethods.WsExTransparent |
                                  NativeMethods.WsExToolWindow |
                                  NativeMethods.WsExNoActivate |
                                  NativeMethods.WsExTopMost;
            return parameters;
        }
    }

    public void ShowCursor(
        CursorObservation cursor,
        double scale,
        byte opacity = 255,
        double preparedMaximumScale = 1.0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!cursor.IsVisible || cursor.Image is null)
        {
            HideOverlay();
            return;
        }

        CursorImage image = cursor.Image;
        (Size size, Point location) = CalculateCursorGeometry(
            image.DisplaySize,
            image.Hotspot,
            cursor.Position,
            scale);
        double safePreparedScale = double.IsFinite(preparedMaximumScale)
            ? Math.Clamp(preparedMaximumScale, 1d, 8d)
            : 1d;
        (Size preparedSize, _) = CalculateCursorGeometry(
            image.DisplaySize,
            image.Hotspot,
            Point.Empty,
            Math.Max(scale, safePreparedScale));
        Graphics graphics = _surface.BeginDraw(preparedSize.Width, preparedSize.Height);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(image.Bitmap, new Rectangle(Point.Empty, size));

        UpdateSurface(location, opacity);
    }

    public void ShowLocator(Point position, double progress, byte opacity = 210)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double value = Math.Clamp(progress, 0d, 1d);
        double dpiScale = DpiUtilities.GetDpiAt(position) / 96d;
        int diameter = (int)Math.Round((48 + (36 * value)) * dpiScale);
        int padding = Math.Max(1, (int)Math.Round(8 * dpiScale));
        int size = diameter + (padding * 2);
        int maximumLocatorSize = (int)Math.Ceiling(100 * dpiScale);
        Graphics graphics = _surface.BeginDraw(maximumLocatorSize, maximumLocatorSize);
        float thickness = (float)((4d + (2d * value)) * dpiScale);
        DrawLocatorRing(graphics, diameter, padding, thickness);

        UpdateSurface(new Point(position.X - (size / 2), position.Y - (size / 2)), opacity);
    }

    internal static void DrawLocatorRing(Graphics graphics, int diameter, int padding, float thickness)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using Pen fox = new(Color.FromArgb(245, 242, 92, 38), thickness);
        RectangleF ring = new(
            padding + (thickness / 2f),
            padding + (thickness / 2f),
            diameter - thickness,
            diameter - thickness);
        graphics.DrawEllipse(fox, ring);

    }

    public void HideOverlay()
    {
        if (!IsVisible || IsDisposed)
        {
            return;
        }

        _ = NativeMethods.ShowWindow(Handle, NativeMethods.SwHide);
        IsVisible = false;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmNcHitTest)
        {
            message.Result = NativeMethods.HtTransparent;
            return;
        }

        if (message.Msg == NativeMethods.WmMouseActivate)
        {
            message.Result = NativeMethods.MaNoActivate;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        HideOverlay();
        _surface.Dispose();
        _disposed = true;
        base.Dispose(disposing);
    }

    internal static (Size Size, Point Location) CalculateCursorGeometry(
        Size bitmapSize,
        Point hotspot,
        Point cursorPosition,
        double requestedScale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bitmapSize.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bitmapSize.Height, 0);

        double safeScale = double.IsFinite(requestedScale)
            ? Math.Clamp(requestedScale, 0.90d, 8d)
            : 1d;
        int width = Math.Clamp(
            (int)Math.Ceiling(bitmapSize.Width * safeScale),
            1,
            MaximumSurfaceDimension);
        int height = Math.Clamp(
            (int)Math.Ceiling(bitmapSize.Height * safeScale),
            1,
            MaximumSurfaceDimension);
        double scaleX = width / (double)bitmapSize.Width;
        double scaleY = height / (double)bitmapSize.Height;
        int left = cursorPosition.X - (int)Math.Round(hotspot.X * scaleX);
        int top = cursorPosition.Y - (int)Math.Round(hotspot.Y * scaleY);
        return (new Size(width, height), new Point(left, top));
    }

    private void UpdateSurface(Point location, byte opacity)
    {
        nint screenDc = NativeMethods.GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            NativeMethods.Point destination = new(location.X, location.Y);
            NativeMethods.Size size = new(_surface.Size.Width, _surface.Size.Height);
            NativeMethods.Point source = new(0, 0);
            NativeMethods.BlendFunction blend = new()
            {
                BlendOperation = NativeMethods.AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = opacity,
                AlphaFormat = NativeMethods.AcSrcAlpha,
            };

            if (!NativeMethods.UpdateLayeredWindow(
                    Handle,
                    screenDc,
                    ref destination,
                    ref size,
                    _surface.DeviceContext,
                    ref source,
                    0,
                    ref blend,
                    NativeMethods.UlwAlpha))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            ReassertTopMostAndShow();
            IsVisible = true;
        }
        finally
        {
            _ = NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    private void ReassertTopMostAndShow()
    {
        // WS_EX_TOPMOST establishes the correct initial band, but another
        // topmost application can subsequently move ahead of the overlay.
        // Reassert on every rendered frame so the cursor remains at the front
        // of the normal interactive desktop without taking keyboard focus.
        const uint flags = NativeMethods.SwpNoMove |
                           NativeMethods.SwpNoSize |
                           NativeMethods.SwpNoActivate |
                           NativeMethods.SwpShowWindow;
        if (!NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopMost,
                0,
                0,
                0,
                0,
                flags))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not restore the cursor overlay to the topmost window band.");
        }
    }
}
