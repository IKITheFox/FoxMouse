using System.Diagnostics;
using FoxMouse.Core;
using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Policy;

public enum TriggerBlockReason
{
    None,
    Dragging,
    MouseButtonPressed = Dragging,
    RemoteSession,
    WindowsMagnifier,
    FullScreenApplication,
    ExcludedApplication,
    NoForegroundWindow,
}

public sealed class InteractionPolicy
{
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;
    private const int MilliDipPerSystemPixel = MotionSample.MilliDipPerDip;
    private static readonly int[] MouseVirtualKeys = [0x01, 0x02, 0x04, 0x05, 0x06];
    private readonly DragClassifier _motionDragClassifier = new();
    private readonly DragClassifier _screenDragClassifier = new();

    internal bool IsPointerDragging =>
        _motionDragClassifier.IsDragging || _screenDragClassifier.IsDragging;

    /// <summary>
    /// Feeds normalized Raw Input into the drag classifier. Call this for every
    /// motion sample, including samples seen while no effect is active, so a
    /// drag is already classified when trigger policy is evaluated.
    /// </summary>
    public void ObservePointer(MotionSample sample)
    {
        int horizontalThreshold = GetMotionDragThreshold(SmCxDrag);
        int verticalThreshold = GetMotionDragThreshold(SmCyDrag);
        if ((sample.Flags & (MotionSampleFlags.Discontinuity | MotionSampleFlags.DeviceChanged)) != 0)
        {
            _motionDragClassifier.Reset();
            _ = _motionDragClassifier.ObserveDelta(
                0,
                0,
                sample.Buttons,
                horizontalThreshold,
                verticalThreshold);
            return;
        }

        _ = _motionDragClassifier.ObserveDelta(
            sample.DeltaXMilliDip,
            sample.DeltaYMilliDip,
            sample.Buttons,
            horizontalThreshold,
            verticalThreshold);
    }

    public void ResetPointerState()
    {
        _motionDragClassifier.Reset();
        _screenDragClassifier.Reset();
    }

    public TriggerBlockReason Evaluate(
        bool disableWhileDragging,
        bool pauseInFullScreen,
        IReadOnlyCollection<string>? excludedProcesses)
    {
        if (!disableWhileDragging)
        {
            _motionDragClassifier.Reset();
            _screenDragClassifier.Reset();
        }
        else if (IsDragging())
        {
            return TriggerBlockReason.Dragging;
        }

        if (NativeMethods.GetSystemMetrics(NativeMethods.SmRemoteSession) != 0)
        {
            return TriggerBlockReason.RemoteSession;
        }

        if (IsWindowsMagnifierRunning())
        {
            return TriggerBlockReason.WindowsMagnifier;
        }

        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return TriggerBlockReason.NoForegroundWindow;
        }

        if (excludedProcesses is { Count: > 0 } && IsExcluded(foreground, excludedProcesses))
        {
            return TriggerBlockReason.ExcludedApplication;
        }

        if (pauseInFullScreen && IsFullScreen(foreground))
        {
            return TriggerBlockReason.FullScreenApplication;
        }

        return TriggerBlockReason.None;
    }

    public static bool IsFullScreen(nint window)
    {
        if (window == nint.Zero || window == NativeMethods.GetShellWindow() ||
            !NativeMethods.GetWindowRect(window, out NativeMethods.Rect windowRect))
        {
            return false;
        }

        if (NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmaCloaked,
                out int cloaked,
                sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        nint monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        NativeMethods.MonitorInfo info = new()
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        const int tolerance = 2;
        return windowRect.Left <= info.Monitor.Left + tolerance &&
               windowRect.Top <= info.Monitor.Top + tolerance &&
               windowRect.Right >= info.Monitor.Right - tolerance &&
               windowRect.Bottom >= info.Monitor.Bottom - tolerance;
    }

    internal static int GetDragRadiusPixels(int systemMetric) =>
        Math.Max(1, (Math.Max(1, systemMetric) + 1) / 2);

    private bool IsDragging()
    {
        PointerButtons buttons = GetPressedButtons();
        bool screenDrag = false;
        if (NativeMethods.GetCursorPos(out NativeMethods.Point position))
        {
            screenDrag = _screenDragClassifier.ObservePosition(
                position.X,
                position.Y,
                buttons,
                GetDragRadiusPixels(NativeMethods.GetSystemMetrics(SmCxDrag)),
                GetDragRadiusPixels(NativeMethods.GetSystemMetrics(SmCyDrag)));
        }
        else if (buttons == PointerButtons.None)
        {
            _screenDragClassifier.Reset();
        }

        return _motionDragClassifier.IsDragging || screenDrag;
    }

    private static int GetMotionDragThreshold(int metric) =>
        checked(GetDragRadiusPixels(NativeMethods.GetSystemMetrics(metric)) * MilliDipPerSystemPixel);

    private static PointerButtons GetPressedButtons()
    {
        PointerButtons buttons = PointerButtons.None;
        if (IsPressed(MouseVirtualKeys[0]))
        {
            buttons |= PointerButtons.Left;
        }

        if (IsPressed(MouseVirtualKeys[1]))
        {
            buttons |= PointerButtons.Right;
        }

        if (IsPressed(MouseVirtualKeys[2]))
        {
            buttons |= PointerButtons.Middle;
        }

        if (IsPressed(MouseVirtualKeys[3]))
        {
            buttons |= PointerButtons.XButton1;
        }

        if (IsPressed(MouseVirtualKeys[4]))
        {
            buttons |= PointerButtons.XButton2;
        }

        return buttons;
    }

    private static bool IsPressed(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsWindowsMagnifierRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("Magnify");
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return true;
        }

        bool found = processes.Length > 0;
        foreach (Process process in processes)
        {
            process.Dispose();
        }

        return found;
    }

    private static bool IsExcluded(nint window, IReadOnlyCollection<string> excludedProcesses)
    {
        _ = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string processName = process.ProcessName;
            return excludedProcesses.Any(
                excluded => string.Equals(
                    Path.GetFileNameWithoutExtension(excluded),
                    processName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
