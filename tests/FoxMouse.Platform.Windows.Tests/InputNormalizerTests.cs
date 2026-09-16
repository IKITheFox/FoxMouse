using System.Drawing;
using FoxMouse.Core;
using FoxMouse.Platform.Windows.Display;
using FoxMouse.Platform.Windows.Input;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class InputNormalizerTests
{
    [Theory]
    [InlineData(-1, 0, true, false, true)]
    [InlineData(0, 999, true, false, false)]
    [InlineData(0, 1_000, true, false, true)]
    [InlineData(2_000, 1_000, true, false, true)]
    [InlineData(0, 1, false, false, true)]
    [InlineData(0, 1, true, true, true)]
    public void DesktopSnapshotSamplingBoundsNativeProbeRate(
        long previous,
        long current,
        bool hasPreviousPosition,
        bool isAbsolute,
        bool expected)
    {
        Assert.Equal(
            expected,
            InputNormalizer.ShouldRefreshDesktopSnapshot(
                previous,
                current,
                hasPreviousPosition,
                isAbsolute));
    }

    [WindowsDesktopFact]
    public void NormalizeCanReadTheInteractiveDesktop()
    {
        InputNormalizer normalizer = new();

        MotionSample sample = normalizer.Normalize(
            new RawMousePacket(
                TimestampMicroseconds: 1,
                Device: new nint(1),
                DeltaX: 1,
                DeltaY: 0,
                Buttons: 0,
                IsAbsolute: false));

        uint dpi = DpiUtilities.GetDpiAt(System.Windows.Forms.Cursor.Position);
        int expectedMilliDip = (int)Math.Round(1_000d * 96d / dpi);
        Assert.Equal(expectedMilliDip, sample.DeltaXMilliDip);
    }

    [Fact]
    public void CalibrationUsesAccumulatedRawMotionInsteadOfOneQueuedPacket()
    {
        double updated = InputNormalizer.CalibrateGain(
            currentGain: 1.0,
            rawX: 100,
            rawY: 0,
            pixelX: 50,
            pixelY: 0,
            dpi: 96);

        Assert.Equal(0.94, updated, precision: 10);
    }

    [Fact]
    public void CalibrationConvertsDesktopPixelsToDip()
    {
        double updated = InputNormalizer.CalibrateGain(
            currentGain: 1.0,
            rawX: 100,
            rawY: 0,
            pixelX: 100,
            pixelY: 0,
            dpi: 192);

        Assert.Equal(0.94, updated, precision: 10);
    }

    [Theory]
    [InlineData(0, 0, 10, 0)]
    [InlineData(10, 0, 0, 0)]
    [InlineData(10, 0, -10, 0)]
    [InlineData(10, 0, 0, 10)]
    public void InvalidOrMismatchedCalibrationKeepsCurrentGain(
        long rawX,
        long rawY,
        int pixelX,
        int pixelY)
    {
        double updated = InputNormalizer.CalibrateGain(
            currentGain: 0.75,
            rawX,
            rawY,
            pixelX,
            pixelY,
            dpi: 96);

        Assert.Equal(0.75, updated);
    }

    [Fact]
    public void BackloggedCursorSnapshotCannotPolluteRawGain()
    {
        InputNormalizer normalizer = new();
        _ = Normalize(normalizer, timestamp: 0, processing: 0, rawX: 0, x: 0);
        MotionSample calibrated = Normalize(
            normalizer,
            timestamp: 10_000,
            processing: 10_000,
            rawX: 100,
            x: 50);
        Assert.Equal(94_000, calibrated.DeltaXMilliDip);

        // Processing fell 99 ms behind the packet clock. GetCursorPos now
        // exposes the final desktop position while this is only the first raw
        // packet in the queued interval.
        MotionSample queued = Normalize(
            normalizer,
            timestamp: 11_000,
            processing: 110_000,
            rawX: 1,
            x: 500);
        Assert.Equal(940, queued.DeltaXMilliDip);

        // Catch the event clock up, then require a fresh moving boundary before
        // calibration resumes.
        _ = Normalize(normalizer, timestamp: 111_000, processing: 110_100, rawX: 100, x: 500);
        MotionSample resync = Normalize(
            normalizer,
            timestamp: 112_000,
            processing: 111_100,
            rawX: 10,
            x: 510);
        Assert.Equal(9_400, resync.DeltaXMilliDip);

        MotionSample afterResync = Normalize(
            normalizer,
            timestamp: 113_000,
            processing: 112_100,
            rawX: 10,
            x: 520);
        Assert.Equal(9_472, afterResync.DeltaXMilliDip);
    }

    [Fact]
    public void RapidQueueDrainRequiresFreshCursorBoundaryBeforeCalibration()
    {
        InputNormalizer normalizer = new();
        _ = Normalize(normalizer, timestamp: 0, processing: 0, rawX: 0, x: 0);

        // Two event timestamps advance at an 8 kHz cadence while the queued
        // handlers run nearly back-to-back. The first position already includes
        // both packets, so neither may establish a calibration ratio.
        MotionSample queued = Normalize(
            normalizer,
            timestamp: 125,
            processing: 10,
            rawX: 10,
            x: 20);
        _ = Normalize(normalizer, timestamp: 250, processing: 20, rawX: 10, x: 20);
        MotionSample resync = Normalize(
            normalizer,
            timestamp: 1_250,
            processing: 1_020,
            rawX: 10,
            x: 30);
        MotionSample calibrated = Normalize(
            normalizer,
            timestamp: 2_250,
            processing: 2_020,
            rawX: 20,
            x: 40);

        Assert.Equal(10_000, queued.DeltaXMilliDip);
        Assert.Equal(10_000, resync.DeltaXMilliDip);
        Assert.Equal(18_800, calibrated.DeltaXMilliDip);
    }

    [Fact]
    public void AbsolutePacketClearsRelativeCalibrationWindow()
    {
        InputNormalizer normalizer = new();
        _ = Normalize(normalizer, timestamp: 0, processing: 0, rawX: 0, x: 0);
        _ = Normalize(normalizer, timestamp: 10_000, processing: 10_000, rawX: 100, x: 0);

        MotionSample absolute = normalizer.NormalizeCore(
            new RawMousePacket(20_000, new nint(1), 0, 0, 0, IsAbsolute: true),
            new Point(500, 0),
            dpi: 96,
            processingTimestampMicroseconds: 20_000);
        MotionSample relative = Normalize(
            normalizer,
            timestamp: 30_000,
            processing: 30_000,
            rawX: 100,
            x: 600);

        Assert.True(absolute.Flags.HasFlag(MotionSampleFlags.Discontinuity));
        Assert.Equal(0, absolute.DeltaXMilliDip);
        Assert.Equal(100_000, relative.DeltaXMilliDip);
    }

    [Fact]
    public void DesktopWarpIsRejectedBeforeItCanChangeGain()
    {
        InputNormalizer normalizer = new();
        _ = Normalize(normalizer, timestamp: 0, processing: 0, rawX: 0, x: 0);
        MotionSample calibrated = Normalize(
            normalizer,
            timestamp: 10_000,
            processing: 10_000,
            rawX: 100,
            x: 50);
        Assert.Equal(94_000, calibrated.DeltaXMilliDip);

        MotionSample warp = Normalize(
            normalizer,
            timestamp: 20_000,
            processing: 20_000,
            rawX: 1,
            x: 2_050);
        MotionSample afterWarp = Normalize(
            normalizer,
            timestamp: 30_000,
            processing: 30_000,
            rawX: 100,
            x: 2_100);

        Assert.True(warp.Flags.HasFlag(MotionSampleFlags.Discontinuity));
        Assert.Equal(940, warp.DeltaXMilliDip);
        Assert.Equal(88_720, afterWarp.DeltaXMilliDip);
    }

    [Fact]
    public void DeviceSwitchKeepsIndependentGainAndDropsCrossDeviceWindow()
    {
        InputNormalizer normalizer = new();
        _ = Normalize(normalizer, timestamp: 0, processing: 0, rawX: 0, x: 0, device: 1);
        MotionSample deviceA = Normalize(
            normalizer,
            timestamp: 10_000,
            processing: 10_000,
            rawX: 100,
            x: 50,
            device: 1);
        Assert.Equal(94_000, deviceA.DeltaXMilliDip);

        MotionSample deviceB = Normalize(
            normalizer,
            timestamp: 20_000,
            processing: 20_000,
            rawX: 100,
            x: 50,
            dpi: 192,
            device: 2);
        MotionSample deviceBAfterCalibration = Normalize(
            normalizer,
            timestamp: 30_000,
            processing: 30_000,
            rawX: 100,
            x: 100,
            dpi: 192,
            device: 2);
        MotionSample deviceAReturn = Normalize(
            normalizer,
            timestamp: 40_000,
            processing: 40_000,
            rawX: 10,
            x: 100,
            device: 1);

        Assert.True(deviceB.Flags.HasFlag(MotionSampleFlags.DeviceChanged));
        Assert.Equal(50_000, deviceB.DeltaXMilliDip);
        Assert.Equal(47_000, deviceBAfterCalibration.DeltaXMilliDip);
        Assert.Equal(9_400, deviceAReturn.DeltaXMilliDip);
    }

    [Fact]
    public void DpiBoundaryScalesGainButNeverCalibratesAcrossMonitors()
    {
        InputNormalizer normalizer = new();
        _ = Normalize(normalizer, timestamp: 0, processing: 0, rawX: 0, x: 0);
        _ = Normalize(normalizer, timestamp: 10_000, processing: 10_000, rawX: 100, x: 100);

        MotionSample boundary = Normalize(
            normalizer,
            timestamp: 20_000,
            processing: 20_000,
            rawX: 100,
            x: 200,
            dpi: 192);
        MotionSample onDestination = Normalize(
            normalizer,
            timestamp: 30_000,
            processing: 30_000,
            rawX: 100,
            x: 300,
            dpi: 192);
        MotionSample returning = Normalize(
            normalizer,
            timestamp: 40_000,
            processing: 40_000,
            rawX: 100,
            x: 400,
            dpi: 96);

        Assert.Equal(50_000, boundary.DeltaXMilliDip);
        Assert.Equal(50_000, onDestination.DeltaXMilliDip);
        Assert.Equal(100_000, returning.DeltaXMilliDip);
        Assert.False(boundary.Flags.HasFlag(MotionSampleFlags.Discontinuity));
    }

    private static MotionSample Normalize(
        InputNormalizer normalizer,
        long timestamp,
        long processing,
        int rawX,
        int x,
        uint dpi = 96,
        int device = 1) =>
        normalizer.NormalizeCore(
            new RawMousePacket(timestamp, new nint(device), rawX, 0, 0, IsAbsolute: false),
            new Point(x, 0),
            dpi,
            processing);
}
