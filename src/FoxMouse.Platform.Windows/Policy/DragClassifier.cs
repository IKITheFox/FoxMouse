using FoxMouse.Core;

namespace FoxMouse.Platform.Windows.Policy;

/// <summary>
/// Classifies a drag from button state plus displacement. Units are supplied by
/// the caller, which lets the same deterministic component consume normalized
/// Raw Input deltas or absolute desktop pixels.
/// </summary>
internal sealed class DragClassifier
{
    private long _positionX;
    private long _positionY;
    private long _anchorX;
    private long _anchorY;
    private PointerButtons _buttons;
    private bool _hasPosition;

    internal bool IsDragging { get; private set; }

    internal bool ObserveDelta(
        long deltaX,
        long deltaY,
        PointerButtons buttons,
        long horizontalThreshold,
        long verticalThreshold)
    {
        _positionX = SaturatingAdd(_positionX, deltaX);
        _positionY = SaturatingAdd(_positionY, deltaY);
        _hasPosition = true;
        return UpdateGesture(buttons, horizontalThreshold, verticalThreshold);
    }

    internal bool ObservePosition(
        long positionX,
        long positionY,
        PointerButtons buttons,
        long horizontalThreshold,
        long verticalThreshold)
    {
        _positionX = positionX;
        _positionY = positionY;
        _hasPosition = true;
        return UpdateGesture(buttons, horizontalThreshold, verticalThreshold);
    }

    internal void Reset()
    {
        _positionX = 0;
        _positionY = 0;
        _anchorX = 0;
        _anchorY = 0;
        _buttons = PointerButtons.None;
        _hasPosition = false;
        IsDragging = false;
    }

    private bool UpdateGesture(
        PointerButtons buttons,
        long horizontalThreshold,
        long verticalThreshold)
    {
        horizontalThreshold = Math.Max(1, horizontalThreshold);
        verticalThreshold = Math.Max(1, verticalThreshold);

        if (!_hasPosition || buttons == PointerButtons.None)
        {
            _buttons = PointerButtons.None;
            IsDragging = false;
            return false;
        }

        // A newly pressed button (or a wholly different button chord) begins a
        // new candidate gesture at the current pointer position. Adding or
        // releasing a secondary button does not cancel an established drag.
        if (_buttons == PointerButtons.None || (_buttons & buttons) == PointerButtons.None)
        {
            _anchorX = _positionX;
            _anchorY = _positionY;
            IsDragging = false;
        }

        _buttons = buttons;
        if (!IsDragging)
        {
            IsDragging = AbsoluteDifference(_positionX, _anchorX) >= (ulong)horizontalThreshold ||
                         AbsoluteDifference(_positionY, _anchorY) >= (ulong)verticalThreshold;
        }

        return IsDragging;
    }

    private static ulong AbsoluteDifference(long left, long right) =>
        left >= right ? (ulong)left - (ulong)right : (ulong)right - (ulong)left;

    private static long SaturatingAdd(long value, long delta)
    {
        if (delta > 0 && value > long.MaxValue - delta)
        {
            return long.MaxValue;
        }

        if (delta < 0 && value < long.MinValue - delta)
        {
            return long.MinValue;
        }

        return value + delta;
    }
}
