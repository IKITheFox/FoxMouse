namespace FoxMouse.Core;

[Flags]
public enum PointerButtons
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Middle = 1 << 2,
    XButton1 = 1 << 3,
    XButton2 = 1 << 4,
}

[Flags]
public enum MotionSampleFlags
{
    None = 0,
    EdgeEstimated = 1 << 0,
    Discontinuity = 1 << 1,
    DeviceChanged = 1 << 2,
}

/// <summary>
/// A platform-neutral pointer movement sample. Time is monotonic microseconds and
/// movement is expressed in milli-DIP (1000 units == 1 DIP).
/// </summary>
public readonly record struct MotionSample(
    long TimestampMicroseconds,
    int DeviceId,
    int DeltaXMilliDip,
    int DeltaYMilliDip,
    PointerButtons Buttons = PointerButtons.None,
    MotionSampleFlags Flags = MotionSampleFlags.None)
{
    public const int MilliDipPerDip = 1_000;
    public const long MicrosecondsPerSecond = 1_000_000;
}
