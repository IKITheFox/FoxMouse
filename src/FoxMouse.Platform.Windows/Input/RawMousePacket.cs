namespace FoxMouse.Platform.Windows.Input;

public readonly record struct RawMousePacket(
    long TimestampMicroseconds,
    nint Device,
    int DeltaX,
    int DeltaY,
    uint Buttons,
    bool IsAbsolute);
