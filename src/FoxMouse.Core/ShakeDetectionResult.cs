namespace FoxMouse.Core;

public readonly record struct ShakeMetrics(
    double PathMilliDip,
    double NetDisplacementMilliDip,
    double RmsSpeedMilliDipPerSecond,
    double DominantAxisRatio,
    int ReversalCount,
    int SampleCount)
{
    public static ShakeMetrics Empty { get; } = new(0, 0, 0, 0.5, 0, 0);

    public double NetToPathRatio => PathMilliDip <= 0
        ? 1.0
        : Math.Clamp(NetDisplacementMilliDip / PathMilliDip, 0.0, 1.0);
}

public readonly record struct ShakeDetectionResult(
    long TimestampMicroseconds,
    int DeviceId,
    double Score,
    bool IsActive,
    bool BecameActive,
    bool BecameInactive,
    bool WasReset,
    ShakeMetrics Metrics)
{
    public static ShakeDetectionResult Empty(long timestampMicroseconds, int deviceId, bool wasReset = false) =>
        new(timestampMicroseconds, deviceId, 0, false, false, false, wasReset, ShakeMetrics.Empty);
}

/// <summary>
/// A point-in-time aggregate of every tracked input device. Unlike an empty
/// result list, this value distinguishes "no device exists" from "the caller's
/// clock was fractionally behind the newest Raw Input packet".
/// </summary>
public readonly record struct ShakeActivitySnapshot(
    long RequestedTimestampMicroseconds,
    long EvaluatedTimestampMicroseconds,
    bool ClockWasClamped,
    int TrackedDeviceCount,
    int ActiveDeviceCount,
    double MaximumActiveScore,
    IReadOnlyList<ShakeDetectionResult> DeviceResults)
{
    public bool HasTrackedDevices => TrackedDeviceCount > 0;

    public bool IsActive => ActiveDeviceCount > 0;
}
