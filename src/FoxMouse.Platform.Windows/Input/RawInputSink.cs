using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Input;

public sealed class RawInputSink : NativeWindow, IDisposable
{
    private const ushort MouseMoveAbsolute = 0x0001;
    private const ushort LeftDown = 0x0001;
    private const ushort LeftUp = 0x0002;
    private const ushort RightDown = 0x0004;
    private const ushort RightUp = 0x0008;
    private const ushort MiddleDown = 0x0010;
    private const ushort MiddleUp = 0x0020;
    private const ushort X1Down = 0x0040;
    private const ushort X1Up = 0x0080;
    private const ushort X2Down = 0x0100;
    private const ushort X2Up = 0x0200;
    private const uint LeftMask = 1;
    private const uint RightMask = 2;
    private const uint MiddleMask = 4;
    private const uint X1Mask = 8;
    private const uint X2Mask = 16;
    internal const long MinimumPacketIntervalMicroseconds = 125;
    private const int MaximumBufferedInputBytes = 1024 * 1024;
    private const int MaximumDrainPassesPerMessage = 8;

    private readonly long _clockOrigin = Stopwatch.GetTimestamp();
    private byte[] _buffer = new byte[128];
    private RawInputTimestampState _timestampState;
    private uint _buttons;
    private bool _disposed;

    public RawInputSink()
    {
        CreateHandle(new CreateParams
        {
            Caption = "FoxMouse.RawInput",
            Parent = NativeMethods.HwndMessage,
        });

        NativeMethods.RawInputDevice[] devices =
        [
            new()
            {
                UsagePage = 0x01,
                Usage = 0x02,
                Flags = NativeMethods.RidevInputSink | NativeMethods.RidevDevNotify,
                Target = Handle,
            },
        ];

        if (!NativeMethods.RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()))
        {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error, "FoxMouse could not register Raw Input.");
        }
    }

    public event EventHandler<RawMousePacket>? PacketReceived;

    public event EventHandler? DeviceTopologyChanged;

    public long CurrentTimestampMicroseconds
    {
        get
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - _clockOrigin;
            return (long)(elapsedTicks * (1_000_000d / Stopwatch.Frequency));
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmInput)
        {
            ReadPacket(message.LParam);
            DrainBufferedPackets();
        }
        else if (message.Msg == NativeMethods.WmInputDeviceChange)
        {
            // RIDEV_DEVNOTIFY reports both arrival and removal. A device
            // arrival is normal during startup and does not invalidate input
            // already owned by another mouse. Only removal can leave tracked
            // button state stale and requires an engine safety transition.
            if (message.WParam == new nint(NativeMethods.GidcRemoval))
            {
                _buttons = 0;
                DeviceTopologyChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Handle != nint.Zero)
        {
            DestroyHandle();
        }

        GC.SuppressFinalize(this);
    }

    private unsafe void ReadPacket(nint rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RawInputHeader>();
        uint queryResult = NativeMethods.GetRawInputData(
            rawInputHandle,
            NativeMethods.RidInput,
            nint.Zero,
            ref size,
            headerSize);
        if (!IsValidRawInputSize(queryResult, size))
        {
            return;
        }

        if (_buffer.Length < size)
        {
            _buffer = new byte[Math.Max((int)size, _buffer.Length * 2)];
        }

        fixed (byte* pointer = _buffer)
        {
            nint data = (nint)pointer;
            uint copied = NativeMethods.GetRawInputData(rawInputHandle, NativeMethods.RidInput, data, ref size, headerSize);
            if (copied == uint.MaxValue || copied != size)
            {
                return;
            }

            NativeMethods.RawInput input = Marshal.PtrToStructure<NativeMethods.RawInput>(data);
            if (input.Header.Size < (uint)Marshal.SizeOf<NativeMethods.RawInput>() ||
                input.Header.Size > copied ||
                input.Header.Type != NativeMethods.RimTypeMouse)
            {
                return;
            }

            EmitMousePacket(input);
        }
    }

    private unsafe void DrainBufferedPackets()
    {
        uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RawInputHeader>();
        for (int pass = 0; pass < MaximumDrainPassesPerMessage; pass++)
        {
            uint bufferBytes = (uint)_buffer.Length;
            uint count;
            fixed (byte* start = _buffer)
            {
                count = NativeMethods.GetRawInputBuffer((nint)start, ref bufferBytes, headerSize);
                if (count == uint.MaxValue)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == NativeMethods.ErrorInsufficientBuffer &&
                        bufferBytes > _buffer.Length &&
                        bufferBytes <= MaximumBufferedInputBytes)
                    {
                        // Leave the fixed block before resizing. The next pass
                        // drains the same queued packets into the larger buffer.
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    if (count == 0)
                    {
                        return;
                    }

                    byte* current = start;
                    byte* end = start + _buffer.Length;
                    for (uint index = 0; index < count; index++)
                    {
                        if (current + sizeof(NativeMethods.RawInputHeader) > end)
                        {
                            return;
                        }

                        NativeMethods.RawInputHeader header = *(NativeMethods.RawInputHeader*)current;
                        if (header.Size < (uint)Marshal.SizeOf<NativeMethods.RawInput>() ||
                            header.Size > MaximumBufferedInputBytes ||
                            current + header.Size > end)
                        {
                            return;
                        }

                        NativeMethods.RawInput input = *(NativeMethods.RawInput*)current;
                        if (input.Header.Type == NativeMethods.RimTypeMouse)
                        {
                            EmitMousePacket(input);
                        }

                        nuint next = AlignRawInputAddress(
                            (nuint)current + header.Size,
                            (nuint)IntPtr.Size);
                        current = (byte*)next;
                    }

                    continue;
                }
            }

            int requested = (int)Math.Min(bufferBytes, MaximumBufferedInputBytes);
            if (requested <= _buffer.Length)
            {
                return;
            }

            _buffer = new byte[Math.Max(requested, Math.Min(_buffer.Length * 2, MaximumBufferedInputBytes))];
        }
    }

    private void EmitMousePacket(NativeMethods.RawInput input)
    {
        UpdateButtons(input.Mouse.ButtonFlags);
        PacketReceived?.Invoke(
            this,
            new RawMousePacket(
                GetPacketTimestampMicroseconds(),
                input.Header.Device,
                input.Mouse.LastX,
                input.Mouse.LastY,
                _buttons,
                (input.Mouse.Flags & MouseMoveAbsolute) != 0));
    }

    internal static nuint AlignRawInputAddress(nuint address, nuint alignment)
    {
        alignment = alignment is 4 or 8 ? alignment : (nuint)IntPtr.Size;
        return (address + alignment - 1) & ~(alignment - 1);
    }

    internal static bool IsValidRawInputSize(uint queryResult, uint size)
    {
        uint minimumSize = (uint)Marshal.SizeOf<NativeMethods.RawInput>();
        return queryResult != uint.MaxValue && size >= minimumSize && size <= 64 * 1024;
    }

    private long GetPacketTimestampMicroseconds()
    {
        uint messageMilliseconds = unchecked((uint)NativeMethods.GetMessageTime());
        uint currentMilliseconds = unchecked((uint)Environment.TickCount64);
        _timestampState = ReconstructPacketTimestamp(
            _timestampState,
            messageMilliseconds,
            currentMilliseconds,
            CurrentTimestampMicroseconds);
        return _timestampState.TimestampMicroseconds;
    }

    internal static RawInputTimestampState ReconstructPacketTimestamp(
        RawInputTimestampState previous,
        uint messageMilliseconds,
        uint currentMilliseconds,
        long currentTimestampMicroseconds)
    {
        long now = Math.Max(0, currentTimestampMicroseconds);
        uint ageMilliseconds = unchecked(currentMilliseconds - messageMilliseconds);
        long wallClockCandidate = ageMilliseconds <= 60_000
            ? Math.Max(0, now - (ageMilliseconds * 1_000L))
            : now;

        if (!previous.HasValue)
        {
            return new RawInputTimestampState(
                true,
                messageMilliseconds,
                wallClockCandidate,
                wallClockCandidate);
        }

        uint messageDeltaMilliseconds = unchecked(messageMilliseconds - previous.MessageMilliseconds);
        long messageBase;
        if (messageDeltaMilliseconds <= 60_000)
        {
            messageBase = SaturatingAdd(
                previous.MessageBaseMicroseconds,
                messageDeltaMilliseconds * 1_000L);
        }
        else
        {
            // A large backwards jump is not a plausible queue delay. Re-anchor
            // instead of turning it into a multi-week unsigned interval.
            messageBase = wallClockCandidate;
        }

        long minimumNext = SaturatingAdd(
            previous.TimestampMicroseconds,
            MinimumPacketIntervalMicroseconds);
        long timestampCandidate = Math.Max(messageBase, minimumNext);

        // GetMessageTime has millisecond resolution, so packets from an 8 kHz
        // mouse need a sub-millisecond ordering hint. Never let that synthetic
        // hint advance beyond the live monotonic clock, however: a future input
        // timestamp makes a render tick look stale and used to end an otherwise
        // active gesture. Equal timestamps are valid and are accumulated by the
        // detector when the consumer drains faster than the hardware cadence.
        long clockCeiling = Math.Max(now, previous.TimestampMicroseconds);
        long timestamp = Math.Min(timestampCandidate, clockCeiling);
        return new RawInputTimestampState(true, messageMilliseconds, messageBase, timestamp);
    }

    private static long SaturatingAdd(long value, long delta) =>
        value > long.MaxValue - delta ? long.MaxValue : value + delta;

    private void UpdateButtons(ushort flags)
    {
        SetButton(flags, LeftDown, LeftUp, LeftMask);
        SetButton(flags, RightDown, RightUp, RightMask);
        SetButton(flags, MiddleDown, MiddleUp, MiddleMask);
        SetButton(flags, X1Down, X1Up, X1Mask);
        SetButton(flags, X2Down, X2Up, X2Mask);
    }

    private void SetButton(ushort flags, ushort down, ushort up, uint mask)
    {
        if ((flags & down) != 0)
        {
            _buttons |= mask;
        }

        if ((flags & up) != 0)
        {
            _buttons &= ~mask;
        }
    }
}

internal readonly record struct RawInputTimestampState(
    bool HasValue,
    uint MessageMilliseconds,
    long MessageBaseMicroseconds,
    long TimestampMicroseconds);
