using System.Reflection;
using System.Runtime.InteropServices;
using FoxMouse.Platform.Windows.Input;
using FoxMouse.Platform.Windows.Interop;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class RawInputSinkTests
{
    private const int WmInputDeviceChange = 0x00FE;

    [Fact]
    public void RawInputSizeValidationRejectsErrorsTruncationAndOversizePackets()
    {
        uint minimum = (uint)Marshal.SizeOf<NativeMethods.RawInput>();

        Assert.False(RawInputSink.IsValidRawInputSize(uint.MaxValue, minimum));
        Assert.False(RawInputSink.IsValidRawInputSize(0, minimum - 1));
        Assert.True(RawInputSink.IsValidRawInputSize(0, minimum));
        Assert.True(RawInputSink.IsValidRawInputSize(0, 64 * 1024));
        Assert.False(RawInputSink.IsValidRawInputSize(0, (64 * 1024) + 1));
    }

    [Fact]
    public void TimestampReconstructionPreservesQueuedMessageTimeAcrossTickCountWrap()
    {
        RawInputTimestampState state = RawInputSink.ReconstructPacketTimestamp(
            default,
            messageMilliseconds: uint.MaxValue - 4,
            currentMilliseconds: uint.MaxValue - 4,
            currentTimestampMicroseconds: 100_000);

        state = RawInputSink.ReconstructPacketTimestamp(
            state,
            messageMilliseconds: 5,
            currentMilliseconds: 5,
            currentTimestampMicroseconds: 110_000);

        Assert.Equal(110_000, state.MessageBaseMicroseconds);
        Assert.Equal(110_000, state.TimestampMicroseconds);
    }

    [Fact]
    public void SameMillisecondPacketsNeverRunAheadOfTheLiveMonotonicClock()
    {
        RawInputTimestampState state = RawInputSink.ReconstructPacketTimestamp(
            default,
            messageMilliseconds: 500,
            currentMilliseconds: 500,
            currentTimestampMicroseconds: 20_000);
        long first = state.TimestampMicroseconds;

        state = RawInputSink.ReconstructPacketTimestamp(state, 500, 500, 20_010);
        long second = state.TimestampMicroseconds;
        state = RawInputSink.ReconstructPacketTimestamp(state, 500, 500, 20_020);
        long third = state.TimestampMicroseconds;

        Assert.InRange(second, first, 20_010);
        Assert.InRange(third, second, 20_020);
    }

    [Fact]
    public void NewMessageMillisecondAdvancesFromStableMessageBaseDuringBacklog()
    {
        RawInputTimestampState state = RawInputSink.ReconstructPacketTimestamp(
            default,
            messageMilliseconds: 900,
            currentMilliseconds: 1_000,
            currentTimestampMicroseconds: 500_000);
        Assert.Equal(400_000, state.TimestampMicroseconds);

        state = RawInputSink.ReconstructPacketTimestamp(
            state,
            messageMilliseconds: 901,
            currentMilliseconds: 1_001,
            currentTimestampMicroseconds: 500_010);

        Assert.Equal(401_000, state.MessageBaseMicroseconds);
        Assert.Equal(401_000, state.TimestampMicroseconds);
    }

    [Fact]
    public void ImplausibleBackwardsMessageTimeReanchorsToCurrentClock()
    {
        RawInputTimestampState state = RawInputSink.ReconstructPacketTimestamp(
            default,
            messageMilliseconds: 100,
            currentMilliseconds: 100,
            currentTimestampMicroseconds: 10_000);

        state = RawInputSink.ReconstructPacketTimestamp(
            state,
            messageMilliseconds: 50,
            currentMilliseconds: 50,
            currentTimestampMicroseconds: 20_000);

        Assert.Equal(20_000, state.MessageBaseMicroseconds);
        Assert.Equal(20_000, state.TimestampMicroseconds);
    }

    [Fact]
    public void EightKilohertzSequenceIsMonotonicAndNeverFutureDated()
    {
        RawInputTimestampState state = default;
        long previous = -1;

        for (var index = 0; index < 8_000; index++)
        {
            long now = 100_000 + (index * RawInputSink.MinimumPacketIntervalMicroseconds);
            uint milliseconds = 1_000u + (uint)(index / 8);
            state = RawInputSink.ReconstructPacketTimestamp(
                state,
                milliseconds,
                milliseconds,
                now);

            Assert.InRange(state.TimestampMicroseconds, previous, now);
            previous = state.TimestampMicroseconds;
        }
    }

    [Theory]
    [InlineData(0x1000UL, 8UL, 0x1000UL)]
    [InlineData(0x1001UL, 8UL, 0x1008UL)]
    [InlineData(0x1007UL, 8UL, 0x1008UL)]
    [InlineData(0x1005UL, 4UL, 0x1008UL)]
    public void BufferedRawInputBlocksUsePointerSizedAlignment(
        ulong address,
        ulong alignment,
        ulong expected)
    {
        nuint actual = RawInputSink.AlignRawInputAddress((nuint)address, (nuint)alignment);

        Assert.Equal((nuint)expected, actual);
    }

    [WindowsDesktopFact]
    public void DeviceTopologyChangeClearsTrackedButtonsBeforeRaisingEvent()
    {
        StaTestThread.Run(() =>
        {
            using RawInputSink sink = new();
            FieldInfo buttonsField = typeof(RawInputSink).GetField(
                                         "_buttons",
                                         BindingFlags.Instance | BindingFlags.NonPublic)
                                     ?? throw new InvalidOperationException("Raw Input button state was not found.");
            buttonsField.SetValue(sink, 0b1_1111u);
            uint stateSeenByHandler = uint.MaxValue;
            sink.DeviceTopologyChanged += (_, _) =>
                stateSeenByHandler = (uint)(buttonsField.GetValue(sink)
                                            ?? throw new InvalidOperationException("Button state was unavailable."));

            _ = SendMessage(
                sink.Handle,
                WmInputDeviceChange,
                new nint(NativeMethods.GidcRemoval),
                nint.Zero);

            Assert.Equal(0u, stateSeenByHandler);
            Assert.Equal(0u, buttonsField.GetValue(sink));
        });
    }

    [WindowsDesktopFact]
    public void DeviceArrivalDoesNotMasqueradeAsDeviceLoss()
    {
        StaTestThread.Run(() =>
        {
            using RawInputSink sink = new();
            FieldInfo buttonsField = typeof(RawInputSink).GetField(
                                         "_buttons",
                                         BindingFlags.Instance | BindingFlags.NonPublic)
                                     ?? throw new InvalidOperationException("Raw Input button state was not found.");
            buttonsField.SetValue(sink, 0b1_1111u);
            int removalNotifications = 0;
            sink.DeviceTopologyChanged += (_, _) => removalNotifications++;

            _ = SendMessage(
                sink.Handle,
                WmInputDeviceChange,
                new nint(NativeMethods.GidcArrival),
                nint.Zero);

            Assert.Equal(0, removalNotifications);
            Assert.Equal(0b1_1111u, buttonsField.GetValue(sink));
        });
    }

    [WindowsDesktopFact]
    public void InitialDeviceEnumerationDoesNotRaiseRemovalNotification()
    {
        StaTestThread.Run(() =>
        {
            using RawInputSink sink = new();
            int removalNotifications = 0;
            sink.DeviceTopologyChanged += (_, _) => removalNotifications++;

            long deadline = Environment.TickCount64 + 750;
            while (Environment.TickCount64 < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }

            Assert.Equal(0, removalNotifications);
        });
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
