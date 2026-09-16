using System.Diagnostics;

namespace FoxMouse.Platform.Windows.Visibility;

/// <summary>
/// Serializes one Guard process's ownership of the no-reference-count
/// MagShowSystemCursor visibility state and its fail-open lease.
/// </summary>
internal sealed class SystemCursorLeaseState
{
    private readonly object _gate = new();
    private readonly ISystemCursorController _controller;
    private readonly Func<long> _getTimestamp;
    private long _activeGeneration;
    private long _leaseDeadline;

    public SystemCursorLeaseState(
        ISystemCursorController controller,
        Func<long>? getTimestamp = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
    }

    public bool IsHidden
    {
        get
        {
            lock (_gate)
            {
                return _controller.IsHiddenByThisController;
            }
        }
    }

    public bool TryHide(long generation, int leaseMilliseconds)
    {
        lock (_gate)
        {
            bool success = _controller.IsHiddenByThisController || _controller.TrySetVisible(false);
            if (success)
            {
                _activeGeneration = generation;
                _leaseDeadline = GetDeadline(leaseMilliseconds);
            }

            return success;
        }
    }

    public bool TryRenew(long generation, int leaseMilliseconds)
    {
        lock (_gate)
        {
            bool success = _controller.IsHiddenByThisController && generation == _activeGeneration;
            if (success)
            {
                _leaseDeadline = GetDeadline(leaseMilliseconds);
            }

            return success;
        }
    }

    public bool TryShow()
    {
        lock (_gate)
        {
            bool success = !_controller.IsHiddenByThisController || _controller.TrySetVisible(true);
            if (success)
            {
                _activeGeneration = 0;
                _leaseDeadline = 0;
            }

            return success;
        }
    }

    public void RestoreIfLeaseExpired(long now)
    {
        lock (_gate)
        {
            if (!_controller.IsHiddenByThisController || now < _leaseDeadline)
            {
                return;
            }

            if (_controller.TrySetVisible(true))
            {
                _activeGeneration = 0;
                _leaseDeadline = 0;
            }
            else
            {
                _leaseDeadline = SaturatingAdd(now, MillisecondsToTicks(100));
            }
        }
    }

    internal long LeaseDeadline
    {
        get
        {
            lock (_gate)
            {
                return _leaseDeadline;
            }
        }
    }

    private long GetDeadline(int requestedMilliseconds)
    {
        int leaseMilliseconds = Math.Clamp(requestedMilliseconds, 200, 2_000);
        return SaturatingAdd(_getTimestamp(), MillisecondsToTicks(leaseMilliseconds));
    }

    internal static long MillisecondsToTicks(int milliseconds) =>
        (long)(milliseconds * (Stopwatch.Frequency / 1000d));

    private static long SaturatingAdd(long value, long increment) =>
        value > long.MaxValue - increment ? long.MaxValue : value + increment;
}
