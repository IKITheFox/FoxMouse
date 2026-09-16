using System.Diagnostics;
using System.Drawing;
using FoxMouse.Core;
using FoxMouse.Platform.Windows.Display;
using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Input;

public sealed class InputNormalizer
{
    private const double InitialRawToDipGain = 1.0;
    private const double GainSmoothing = 0.12;
    private const double MinimumRawToDipGain = 0.001;
    private const double MaximumRawToDipGain = 8.0;
    private const double MaximumDesktopJump = 1_500;
    private const long CalibrationBacklogToleranceMicroseconds = 2_000;
    private const long MaximumTrackedBacklogMicroseconds = 5_000_000;
    internal const long DesktopSnapshotIntervalMicroseconds = 1_000;
    private readonly long _processingClockOrigin = Stopwatch.GetTimestamp();
    private readonly Dictionary<nint, DeviceState> _devices = [];
    private Point? _lastCursorPosition;
    private uint _lastCursorDpi;
    private nint _calibrationDevice;
    private long _lastPacketTimestampMicroseconds = -1;
    private long _lastProcessingTimestampMicroseconds = -1;
    private long _estimatedBacklogMicroseconds;
    private long _lastDesktopSnapshotMicroseconds = -1;
    private nint _lastDpiMonitor;
    private bool _calibrationResyncRequired;
    private int _nextDeviceId;

    public MotionSample Normalize(RawMousePacket packet)
    {
        long processingTimestampMicroseconds = CurrentProcessingTimestampMicroseconds;
        Point position = _lastCursorPosition ?? Point.Empty;
        uint dpi = _lastCursorDpi is >= 48 and <= 768 ? _lastCursorDpi : 96u;

        // Raw deltas, not GetCursorPos, drive gesture detection. At 8 kHz the
        // desktop position and monitor DPI cannot usefully change 8,000 times a
        // second, so sample that calibration state at 1 kHz and accumulate raw
        // counts between probes. This removes two native calls from most hot-
        // path packets without losing any motion.
        if (ShouldRefreshDesktopSnapshot(
                _lastDesktopSnapshotMicroseconds,
                processingTimestampMicroseconds,
                _lastCursorPosition.HasValue,
                packet.IsAbsolute))
        {
            if (NativeMethods.GetCursorPos(out NativeMethods.Point nativePosition))
            {
                position = new Point(nativePosition.X, nativePosition.Y);
                NativeMethods.Point monitorPoint = new(position.X, position.Y);
                nint monitor = NativeMethods.MonitorFromPoint(
                    monitorPoint,
                    NativeMethods.MonitorDefaultToNearest);
                if (monitor != _lastDpiMonitor || _lastCursorDpi is < 48 or > 768)
                {
                    dpi = DpiUtilities.GetDpiAt(position);
                    _lastDpiMonitor = monitor;
                }
            }

            _lastDesktopSnapshotMicroseconds = processingTimestampMicroseconds;
        }

        return NormalizeCore(packet, position, dpi, processingTimestampMicroseconds);
    }

    internal MotionSample NormalizeCore(
        RawMousePacket packet,
        Point position,
        uint dpi,
        long processingTimestampMicroseconds)
    {
        dpi = dpi is >= 48 and <= 768 ? dpi : 96u;
        bool isNewDevice = !_devices.TryGetValue(packet.Device, out DeviceState? existingState);
        DeviceState state;
        if (isNewDevice)
        {
            state = new DeviceState(_nextDeviceId++);
            _devices.Add(packet.Device, state);
        }
        else
        {
            state = existingState!;
        }

        state.AdaptGainToDpi(dpi);
        bool calibrationTimingReliable = UpdateBacklogEstimate(
            packet.TimestampMicroseconds,
            processingTimestampMicroseconds);

        double deltaXDip = 0;
        double deltaYDip = 0;
        MotionSampleFlags flags = isNewDevice ? MotionSampleFlags.DeviceChanged : MotionSampleFlags.None;
        if (packet.IsAbsolute)
        {
            flags |= MotionSampleFlags.Discontinuity;
            ResetCalibrationWindows();
            _calibrationDevice = packet.Device;
            _calibrationResyncRequired = false;
        }
        else
        {
            // Detection is driven by each Raw Input packet, not by repeatedly
            // sampling GetCursorPos. GetCursorPos reports the newest desktop
            // position and can therefore collapse an 8 kHz backlog into the
            // first queued packet. The desktop delta is used only to calibrate
            // the raw-count-to-DIP gain over a matching accumulation window.
            bool calibrationDeviceChanged = _calibrationDevice != packet.Device;
            if (calibrationDeviceChanged)
            {
                ResetCalibrationWindows();
                _calibrationDevice = packet.Device;
                _lastCursorPosition = position;
                _lastCursorDpi = dpi;
            }

            bool dpiChanged = _lastCursorDpi != 0 && _lastCursorDpi != dpi;
            if (!calibrationTimingReliable)
            {
                ResetCalibrationWindows();
                _calibrationResyncRequired = true;
            }
            else if (dpiChanged)
            {
                // A physical-pixel delta crossing monitors cannot be converted
                // with either endpoint DPI alone. Preserve the per-device gain,
                // scale it to the destination DPI and start a fresh window.
                ResetCalibrationWindows();
            }
            else if (calibrationDeviceChanged)
            {
                // The cursor position already includes this device's current
                // packet. Do not carry that raw delta into the next window.
            }
            else if (_calibrationResyncRequired)
            {
                ResetCalibrationWindows();
                if (_lastCursorPosition is Point resyncPosition && resyncPosition != position)
                {
                    // The first fresh desktop movement establishes a new
                    // cursor/raw boundary. Calibrate starting with the next
                    // packet so no queued raw counts leak into the window.
                    _calibrationResyncRequired = false;
                }
            }
            else if (_lastCursorPosition is Point previous)
            {
                state.AccumulatedRawX += packet.DeltaX;
                state.AccumulatedRawY += packet.DeltaY;
                int pixelX = position.X - previous.X;
                int pixelY = position.Y - previous.Y;
                if (pixelX != 0 || pixelY != 0)
                {
                    double pixelLength = Length(pixelX, pixelY);
                    double screenLength = Length(
                        DpiUtilities.PixelsToDip(pixelX, dpi),
                        DpiUtilities.PixelsToDip(pixelY, dpi));
                    if (pixelLength > MaximumDesktopJump ||
                        screenLength > MaximumDesktopJump ||
                        !TryCalibrateGain(
                            state.RawToDipGain,
                            state.AccumulatedRawX,
                            state.AccumulatedRawY,
                            pixelX,
                            pixelY,
                            dpi,
                            out double calibratedGain))
                    {
                        flags |= MotionSampleFlags.Discontinuity;
                    }
                    else
                    {
                        state.RawToDipGain = calibratedGain;
                    }

                    state.ResetCalibration();
                    _lastCursorPosition = position;
                }
                else if (packet.DeltaX != 0 || packet.DeltaY != 0)
                {
                    flags |= MotionSampleFlags.EdgeEstimated;
                }

            }

            deltaXDip = packet.DeltaX * state.RawToDipGain;
            deltaYDip = packet.DeltaY * state.RawToDipGain;
        }

        _lastCursorPosition = position;
        _lastCursorDpi = dpi;
        return new MotionSample(
            packet.TimestampMicroseconds,
            state.DeviceId,
            ToMilliDip(deltaXDip),
            ToMilliDip(deltaYDip),
            (PointerButtons)(packet.Buttons & 0x1F),
            flags);
    }

    public void Reset()
    {
        _devices.Clear();
        _lastCursorPosition = null;
        _lastCursorDpi = 0;
        _calibrationDevice = nint.Zero;
        _lastPacketTimestampMicroseconds = -1;
        _lastProcessingTimestampMicroseconds = -1;
        _estimatedBacklogMicroseconds = 0;
        _lastDesktopSnapshotMicroseconds = -1;
        _lastDpiMonitor = nint.Zero;
        _calibrationResyncRequired = false;
        _nextDeviceId = 0;
    }

    internal static double CalibrateGain(
        double currentGain,
        long rawX,
        long rawY,
        int pixelX,
        int pixelY,
        uint dpi)
    {
        return TryCalibrateGain(
            currentGain,
            rawX,
            rawY,
            pixelX,
            pixelY,
            dpi,
            out double calibratedGain)
            ? calibratedGain
            : currentGain;
    }

    internal static bool ShouldRefreshDesktopSnapshot(
        long previousTimestampMicroseconds,
        long currentTimestampMicroseconds,
        bool hasPreviousPosition,
        bool isAbsolutePacket) =>
        !hasPreviousPosition ||
        isAbsolutePacket ||
        previousTimestampMicroseconds < 0 ||
        currentTimestampMicroseconds < previousTimestampMicroseconds ||
        currentTimestampMicroseconds - previousTimestampMicroseconds
            >= DesktopSnapshotIntervalMicroseconds;

    private static bool TryCalibrateGain(
        double currentGain,
        long rawX,
        long rawY,
        int pixelX,
        int pixelY,
        uint dpi,
        out double calibratedGain)
    {
        calibratedGain = currentGain;
        double rawLength = Length(rawX, rawY);
        double dipX = DpiUtilities.PixelsToDip(pixelX, dpi);
        double dipY = DpiUtilities.PixelsToDip(pixelY, dpi);
        double screenLength = Length(dipX, dipY);
        if (rawLength <= 0 || screenLength <= 0)
        {
            return false;
        }

        double alignment = ((rawX * dipX) + (rawY * dipY)) / (rawLength * screenLength);
        if (!double.IsFinite(alignment) || alignment < 0.5)
        {
            return false;
        }

        double observedGain = screenLength / rawLength;
        if (!double.IsFinite(observedGain) ||
            observedGain < MinimumRawToDipGain ||
            observedGain > MaximumRawToDipGain)
        {
            return false;
        }

        calibratedGain = currentGain + ((observedGain - currentGain) * GainSmoothing);
        return true;
    }

    private long CurrentProcessingTimestampMicroseconds
    {
        get
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - _processingClockOrigin;
            return (long)(elapsedTicks * (1_000_000d / Stopwatch.Frequency));
        }
    }

    private bool UpdateBacklogEstimate(long packetTimestampMicroseconds, long processingTimestampMicroseconds)
    {
        bool reliable = true;
        if (_lastPacketTimestampMicroseconds >= 0 && _lastProcessingTimestampMicroseconds >= 0)
        {
            if (packetTimestampMicroseconds <= _lastPacketTimestampMicroseconds ||
                processingTimestampMicroseconds < _lastProcessingTimestampMicroseconds)
            {
                _estimatedBacklogMicroseconds = 0;
                reliable = false;
            }
            else
            {
                long packetDelta = packetTimestampMicroseconds - _lastPacketTimestampMicroseconds;
                long processingDelta = processingTimestampMicroseconds - _lastProcessingTimestampMicroseconds;
                bool drainingFasterThanEventClock = processingDelta < packetDelta / 2;
                if (processingDelta >= packetDelta)
                {
                    long addedLag = processingDelta - packetDelta;
                    _estimatedBacklogMicroseconds = Math.Min(
                        MaximumTrackedBacklogMicroseconds,
                        SaturatingAdd(_estimatedBacklogMicroseconds, addedLag));
                }
                else
                {
                    long recoveredLag = packetDelta - processingDelta;
                    _estimatedBacklogMicroseconds = Math.Max(
                        0,
                        _estimatedBacklogMicroseconds - recoveredLag);
                }

                reliable = _estimatedBacklogMicroseconds <= CalibrationBacklogToleranceMicroseconds &&
                           !drainingFasterThanEventClock;
            }
        }

        _lastPacketTimestampMicroseconds = packetTimestampMicroseconds;
        _lastProcessingTimestampMicroseconds = processingTimestampMicroseconds;
        return reliable;
    }

    private void ResetCalibrationWindows()
    {
        foreach (DeviceState device in _devices.Values)
        {
            device.ResetCalibration();
        }
    }

    private static long SaturatingAdd(long value, long delta) =>
        value > long.MaxValue - delta ? long.MaxValue : value + delta;

    private static int ToMilliDip(double dip)
    {
        double milliDip = Math.Round(dip * MotionSample.MilliDipPerDip);
        return (int)Math.Clamp(milliDip, -2_000_000d, 2_000_000d);
    }

    private static double Length(double x, double y) => Math.Sqrt((x * x) + (y * y));

    private sealed class DeviceState(int deviceId)
    {
        private uint _gainDpi = 96;

        internal int DeviceId { get; } = deviceId;

        internal double RawToDipGain { get; set; } = InitialRawToDipGain;

        internal long AccumulatedRawX { get; set; }

        internal long AccumulatedRawY { get; set; }

        internal void AdaptGainToDpi(uint dpi)
        {
            if (dpi == _gainDpi)
            {
                return;
            }

            RawToDipGain = Math.Clamp(
                RawToDipGain * (_gainDpi / (double)dpi),
                MinimumRawToDipGain,
                MaximumRawToDipGain);
            _gainDpi = dpi;
        }

        internal void ResetCalibration()
        {
            AccumulatedRawX = 0;
            AccumulatedRawY = 0;
        }
    }
}
