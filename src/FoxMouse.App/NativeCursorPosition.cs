using System.Runtime.InteropServices;

namespace FoxMouse.App;

internal static class NativeCursorPosition
{
    internal static bool TryGet(out System.Drawing.Point position)
    {
        if (GetCursorPos(out Point point))
        {
            position = new System.Drawing.Point(point.X, point.Y);
            return true;
        }

        position = default;
        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }
}
