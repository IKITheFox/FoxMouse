using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Display;

public static class DpiUtilities
{
    public static uint GetDpiAt(System.Drawing.Point point)
    {
        NativeMethods.Point nativePoint = new(point.X, point.Y);
        // GetDpiForMonitor is not DPI-awareness aware and Microsoft does not
        // recommend it for Per-Monitor-v2 callers. Resolve the window beneath
        // the pointer first; GetDpiForWindow returns that window's actual DPI
        // regardless of the calling thread's awareness context.
        nint window = NativeMethods.WindowFromPoint(nativePoint);
        if (window != nint.Zero)
        {
            try
            {
                uint windowDpi = NativeMethods.GetDpiForWindow(window);
                if (windowDpi is >= 48 and <= 768)
                {
                    return windowDpi;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        nint monitor = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MonitorDefaultToNearest);
        if (monitor != nint.Zero)
        {
            try
            {
                int result = NativeMethods.GetDpiForMonitor(
                    monitor,
                    NativeMethods.MonitorDpiType.Effective,
                    out uint dpiX,
                    out _);
                if (result == 0 && dpiX >= 48 && dpiX <= 768)
                {
                    return dpiX;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        uint systemDpi = NativeMethods.GetDpiForSystem();
        return systemDpi == 0 ? 96u : systemDpi;
    }

    public static double PixelsToDip(double pixels, uint dpi) => pixels * 96d / Math.Max(1u, dpi);
}
