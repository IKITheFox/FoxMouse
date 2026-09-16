using System.Runtime.InteropServices;

namespace FoxMouse.Platform.Windows.Interop;

internal static class NativeMethods
{
    internal const int CursorShowing = 0x00000001;
    internal const uint RidInput = 0x10000003;
    internal const int ErrorInsufficientBuffer = 122;
    internal const uint RimTypeMouse = 0;
    internal const uint RidevInputSink = 0x00000100;
    internal const uint RidevDevNotify = 0x00002000;
    internal const int WmInput = 0x00FF;
    internal const int WmInputDeviceChange = 0x00FE;
    internal const int GidcArrival = 1;
    internal const int GidcRemoval = 2;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmMouseActivate = 0x0021;
    internal const uint WmSetCursor = 0x0020;
    internal const uint WmMouseMove = 0x0200;
    internal const int HtTransparent = -1;
    internal const int HtClient = 1;
    internal const int MaNoActivate = 3;
    internal const int SwHide = 0;
    internal const int SwShowNoActivate = 4;
    internal const int WsExLayered = 0x00080000;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExTopMost = 0x00000008;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const byte AcSrcOver = 0;
    internal const byte AcSrcAlpha = 1;
    internal const uint UlwAlpha = 0x00000002;
    internal const uint MonitorDefaultToNearest = 2;
    internal const int GwlExStyle = -20;
    internal const int SmRemoteSession = 0x1000;
    internal const int DwmaCloaked = 14;
    internal const uint SmtoBlock = 0x0001;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const uint SmtoErrorOnExit = 0x0020;
    internal static readonly nint HwndMessage = new(-3);
    internal static readonly nint HwndTopMost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        nint rawInput,
        uint command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputBuffer(
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint CopyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetIconInfo(nint icon, out IconInfo iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetDC(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ReleaseDC(nint window, nint dc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(
        nint window,
        nint destinationDc,
        ref Point destinationPoint,
        ref Size size,
        nint sourceDc,
        ref Point sourcePoint,
        uint colorKey,
        ref BlendFunction blend,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern int GetMessageTime();

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true, SetLastError = true)]
    internal static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint messageResult);

    [DllImport("user32.dll")]
    internal static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SelectObject(nint dc, nint objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint objectHandle);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetObject(nint objectHandle, int objectSize, out BitmapInfo bitmap);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint monitor, MonitorDpiType type, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
    internal static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
    internal static extern uint TimeEndPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;

        internal Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size
    {
        internal int Width;
        internal int Height;

        internal Size(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nint WParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct RawMouse
    {
        [FieldOffset(0)] internal ushort Flags;
        [FieldOffset(4)] internal uint Buttons;
        [FieldOffset(4)] internal ushort ButtonFlags;
        [FieldOffset(6)] internal ushort ButtonData;
        [FieldOffset(8)] internal uint RawButtons;
        [FieldOffset(12)] internal int LastX;
        [FieldOffset(16)] internal int LastY;
        [FieldOffset(20)] internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInput
    {
        internal RawInputHeader Header;
        internal RawMouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorInfo
    {
        internal uint Size;
        internal int Flags;
        internal nint Cursor;
        internal Point ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] internal bool IsIcon;
        internal uint HotspotX;
        internal uint HotspotY;
        internal nint MaskBitmap;
        internal nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal int Type;
        internal int Width;
        internal int Height;
        internal int WidthBytes;
        internal ushort Planes;
        internal ushort BitsPixel;
        internal nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendFunction
    {
        internal byte BlendOperation;
        internal byte BlendFlags;
        internal byte SourceConstantAlpha;
        internal byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;
    }

    internal enum MonitorDpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2,
    }
}
