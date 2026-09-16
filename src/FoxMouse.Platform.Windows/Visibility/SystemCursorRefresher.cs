using System.Drawing;
using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Visibility;

/// <summary>
/// Reasserts the cursor selected by the window beneath the stationary pointer.
/// This is needed after MagShowSystemCursor(TRUE): Windows otherwise may wait
/// for the next physical mouse move before painting the cursor again.
/// </summary>
public sealed class SystemCursorRefresher : ISystemCursorRefresher
{
    internal const uint MessageTimeoutMilliseconds = 25;

    private readonly ISystemCursorRefreshBackend _backend;

    public SystemCursorRefresher()
        : this(new Win32SystemCursorRefreshBackend())
    {
    }

    internal SystemCursorRefresher(ISystemCursorRefreshBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public bool TryRefreshAtCurrentPosition()
    {
        try
        {
            _ = TryRequestCursorFromOwningWindow();
        }
        catch (Exception)
        {
            // UIPI, a concurrently destroyed window, or a failing display
            // driver falls through to the global-cursor fallback below.
        }

        bool cursorReapplied = false;
        try
        {
            // Reapply only the valid, shared HCURSOR reported by Windows. Do
            // not set NULL, destroy the handle, or perturb the pointer position.
            cursorReapplied = _backend.TryReapplyCurrentCursor();
        }
        catch (Exception)
        {
            // Redraw failure must not change successful visibility ownership.
        }

        // Message delivery alone says nothing about cursor visibility.
        return cursorReapplied;
    }

    private bool TryRequestCursorFromOwningWindow()
    {
        if (!_backend.TryGetCursorPosition(out Point position))
        {
            return false;
        }

        nint window = _backend.GetWindowAt(position);
        if (window == nint.Zero)
        {
            return false;
        }

        int hitTest = NativeMethods.HtClient;
        if (_backend.TrySendMessageWithTimeout(
                window,
                NativeMethods.WmNcHitTest,
                nint.Zero,
                PackPosition(position),
                out nint hitTestResult))
        {
            hitTest = unchecked((int)hitTestResult);
        }

        return _backend.TrySendMessageWithTimeout(
            window,
            NativeMethods.WmSetCursor,
            window,
            PackSetCursorParameters(hitTest),
            out _);
    }

    internal static nint PackPosition(Point position) => unchecked((nint)(
        (uint)(ushort)position.X |
        ((uint)(ushort)position.Y << 16)));

    internal static nint PackSetCursorParameters(int hitTest) => unchecked((nint)(
        (uint)(ushort)hitTest |
        (NativeMethods.WmMouseMove << 16)));
}

internal interface ISystemCursorRefreshBackend
{
    bool TryGetCursorPosition(out Point position);

    nint GetWindowAt(Point position);

    bool TrySendMessageWithTimeout(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        out nint result);

    bool TryReapplyCurrentCursor();
}

internal sealed class Win32SystemCursorRefreshBackend : ISystemCursorRefreshBackend
{
    public bool TryGetCursorPosition(out Point position)
    {
        bool succeeded = NativeMethods.GetCursorPos(out NativeMethods.Point nativePosition);
        position = succeeded
            ? new Point(nativePosition.X, nativePosition.Y)
            : Point.Empty;
        return succeeded;
    }

    public nint GetWindowAt(Point position) => NativeMethods.WindowFromPoint(
        new NativeMethods.Point(position.X, position.Y));

    public bool TrySendMessageWithTimeout(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        out nint result)
    {
        nint delivered = NativeMethods.SendMessageTimeout(
            window,
            message,
            wParam,
            lParam,
            NativeMethods.SmtoBlock |
            NativeMethods.SmtoAbortIfHung |
            NativeMethods.SmtoErrorOnExit,
            SystemCursorRefresher.MessageTimeoutMilliseconds,
            out nuint nativeResult);
        result = unchecked((nint)nativeResult);
        return delivered != nint.Zero;
    }

    public bool TryReapplyCurrentCursor()
    {
        NativeMethods.CursorInfo info = new()
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CursorInfo>(),
        };
        if (!NativeMethods.GetCursorInfo(ref info) ||
            (info.Flags & 2u) != 0 || // CURSOR_SUPPRESSED: respect touch/pen suppression.
            info.Cursor == nint.Zero)
        {
            return false;
        }

        _ = NativeMethods.SetCursor(info.Cursor);
        return NativeMethods.GetCursorInfo(ref info) &&
            (info.Flags & NativeMethods.CursorShowing) != 0 && info.Cursor != nint.Zero;
    }
}
