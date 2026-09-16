using System.Drawing;
using FoxMouse.Platform.Windows.Interop;
using FoxMouse.Platform.Windows.Visibility;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class SystemCursorRefresherTests
{
    [Fact]
    public void DeliveredMessageDoesNotProveCursorRecovery()
    {
        FakeRefreshBackend backend = new() { MessageDeliverySucceeds = true, ReapplySucceeds = false };
        Assert.False(new SystemCursorRefresher(backend).TryRefreshAtCurrentPosition());
    }
    [Fact]
    public void RefreshAsksUnderlyingWindowThenReappliesCurrentSharedCursor()
    {
        FakeRefreshBackend backend = new()
        {
            Position = new Point(-17, 245),
            Window = new nint(0x1234),
            HitTest = 7,
            MessageDeliverySucceeds = true,
            ReapplySucceeds = true,
        };
        SystemCursorRefresher refresher = new(backend);

        Assert.True(refresher.TryRefreshAtCurrentPosition());

        Assert.Equal(2, backend.Messages.Count);
        Assert.Equal((uint)NativeMethods.WmNcHitTest, backend.Messages[0].Message);
        Assert.Equal(
            SystemCursorRefresher.PackPosition(backend.Position),
            backend.Messages[0].LParam);
        Assert.Equal(NativeMethods.WmSetCursor, backend.Messages[1].Message);
        Assert.Equal(backend.Window, backend.Messages[1].WParam);
        Assert.Equal(
            SystemCursorRefresher.PackSetCursorParameters(backend.HitTest),
            backend.Messages[1].LParam);
        Assert.Equal(1, backend.ReapplyCount);
        Assert.InRange(SystemCursorRefresher.MessageTimeoutMilliseconds, 1u, 50u);
    }

    [Fact]
    public void UipiMessageFailureFallsBackToCurrentSharedCursor()
    {
        FakeRefreshBackend backend = new()
        {
            Window = new nint(0x5678),
            MessageDeliverySucceeds = false,
            ReapplySucceeds = true,
        };
        SystemCursorRefresher refresher = new(backend);

        Assert.True(refresher.TryRefreshAtCurrentPosition());

        Assert.Equal(2, backend.Messages.Count);
        Assert.Equal(1, backend.ReapplyCount);
    }

    [Fact]
    public void CompleteRefreshFailureIsContained()
    {
        FakeRefreshBackend backend = new()
        {
            Window = nint.Zero,
            ReapplySucceeds = false,
        };
        SystemCursorRefresher refresher = new(backend);

        Assert.False(refresher.TryRefreshAtCurrentPosition());
        Assert.Empty(backend.Messages);
        Assert.Equal(1, backend.ReapplyCount);
    }

    [Fact]
    public void NativeBackendExceptionIsContainedWithoutMovingPointer()
    {
        FakeRefreshBackend backend = new()
        {
            ThrowOnWindowLookup = true,
            ReapplySucceeds = true,
        };
        SystemCursorRefresher refresher = new(backend);

        Assert.True(refresher.TryRefreshAtCurrentPosition());

        Assert.Equal(backend.Position, backend.LastRequestedPosition);
        Assert.Equal(1, backend.ReapplyCount);
    }

    [Fact]
    public void RefreshFallbackExceptionIsContained()
    {
        FakeRefreshBackend backend = new()
        {
            Window = nint.Zero,
            ThrowOnReapply = true,
        };
        SystemCursorRefresher refresher = new(backend);

        Assert.False(refresher.TryRefreshAtCurrentPosition());

        Assert.Empty(backend.Messages);
        Assert.Equal(1, backend.ReapplyCount);
    }

    private sealed class FakeRefreshBackend : ISystemCursorRefreshBackend
    {
        public Point Position { get; init; } = new(100, 200);

        public Point LastRequestedPosition { get; private set; }

        public nint Window { get; init; } = new(1);

        public int HitTest { get; init; } = NativeMethods.HtClient;

        public bool MessageDeliverySucceeds { get; init; }

        public bool ReapplySucceeds { get; init; }

        public bool ThrowOnWindowLookup { get; init; }

        public bool ThrowOnReapply { get; init; }

        public int ReapplyCount { get; private set; }

        public List<SentMessage> Messages { get; } = [];

        public bool TryGetCursorPosition(out Point position)
        {
            position = Position;
            LastRequestedPosition = position;
            return true;
        }

        public nint GetWindowAt(Point position)
        {
            LastRequestedPosition = position;
            if (ThrowOnWindowLookup)
            {
                throw new InvalidOperationException("simulated window teardown race");
            }

            return Window;
        }

        public bool TrySendMessageWithTimeout(
            nint window,
            uint message,
            nint wParam,
            nint lParam,
            out nint result)
        {
            Messages.Add(new SentMessage(window, message, wParam, lParam));
            result = message == NativeMethods.WmNcHitTest ? new nint(HitTest) : new nint(1);
            return MessageDeliverySucceeds;
        }

        public bool TryReapplyCurrentCursor()
        {
            ReapplyCount++;
            if (ThrowOnReapply)
            {
                throw new InvalidOperationException("simulated cursor reapply failure");
            }

            return ReapplySucceeds;
        }
    }

    private sealed record SentMessage(nint Window, uint Message, nint WParam, nint LParam);
}
