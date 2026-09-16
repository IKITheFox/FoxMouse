namespace FoxMouse.Core;

public enum EffectPhase
{
    Idle,
    PreparingOverlay,
    ArmingGuard,
    AwaitingHideAcknowledgement,
    NativeHidden,
    Releasing,
    AwaitingShowAcknowledgement,
    Faulted,
}

public enum NativeCursorVisibility
{
    KnownShown,
    KnownHidden,
    Unknown,
}

/// <summary>
/// Describes how a fault affects future high-fidelity attempts. The default
/// disposition is resolved from the event kind by the state machine.
/// </summary>
public enum FaultDisposition
{
    Default,
    Transient,
    Recoverable,
    Permanent,
}

/// <summary>
/// Circuit-breaker state for high-fidelity cursor replacement. CoolingDown
/// rejects attempts until its deadline; the first attempt after that deadline
/// is HalfOpen and proves the complete overlay/guard/hide path before returning
/// to Healthy.
/// </summary>
public enum EffectRecoveryStatus
{
    Healthy,
    CoolingDown,
    HalfOpen,
    PermanentlyDisabled,
}

/// <summary>
/// Stable, allocation-free fault reasons that can be forwarded to diagnostics.
/// </summary>
public enum EffectFaultReason
{
    None,
    OverlayFailed,
    OverlayPreparationFailed,
    GuardArmFailed,
    HideFailed,
    DeviceLost,
    HeartbeatLost,
    GuardExited,
    CursorInvalid,
    LeaseExpired,
    SessionLocked,
    Suspended,
    DisplayChanging,
    UserDisabled,
    Shutdown,
    Unspecified,
}

public enum EffectEventKind
{
    Trigger,
    ReleaseRequested,
    OverlayReady,
    OverlayFailed,
    GuardArmedAcknowledged,
    HideAcknowledged,
    OverlayAtRest,
    ShowAcknowledged,
    LeaseRenewed,
    Tick,
    Disable,
    Shutdown,
    SessionLock,
    Suspend,
    DisplayChanging,
    DeviceLost,
    HeartbeatLost,
    GuardExited,
    CursorInvalid,
    ResetFault,
}

public enum EffectCommandKind
{
    PrepareOverlay,
    ArmGuard,
    RequestHideNative,
    BeginGrow,
    BeginRelease,
    RequestShowNative,
    RemoveOverlay,
    RefreshNativeCursor,
    DisableHighFidelity,
    CompleteShutdown,
}

public readonly record struct EffectEvent(
    EffectEventKind Kind,
    long TimestampMicroseconds,
    long Generation = 0,
    bool Success = true,
    double Score = 0,
    EffectFaultReason Reason = EffectFaultReason.None,
    FaultDisposition Disposition = FaultDisposition.Default);

public readonly record struct EffectCommand(EffectCommandKind Kind, long Generation);

public sealed record EffectState
{
    public static EffectState Initial { get; } = new();

    public EffectPhase Phase { get; init; } = EffectPhase.Idle;

    public long Generation { get; init; }

    public NativeCursorVisibility NativeVisibility { get; init; } = NativeCursorVisibility.KnownShown;

    public bool OverlayReady { get; init; }

    public bool GuardArmed { get; init; }

    public bool HighFidelityDisabled { get; init; }

    public EffectRecoveryStatus RecoveryStatus { get; init; } = EffectRecoveryStatus.Healthy;

    public long RecoveryDeadlineMicroseconds { get; init; }

    public int ConsecutiveRecoverableFaults { get; init; }

    public EffectFaultReason LastFaultReason { get; init; }

    public FaultDisposition LastFaultDisposition { get; init; }

    public long LastFaultTimestampMicroseconds { get; init; }

    public bool ShutdownRequested { get; init; }

    public long LeaseDeadlineMicroseconds { get; init; }

    public long NextShowRetryMicroseconds { get; init; }
}

public sealed record EffectStateMachineOptions
{
    public static EffectStateMachineOptions Default { get; } = new();

    public long HiddenLeaseMicroseconds { get; init; } = 1_200_000;

    public long ShowRetryMicroseconds { get; init; } = 100_000;

    public long RecoveryCooldownMicroseconds { get; init; } = 250_000;

    public long MaximumRecoveryCooldownMicroseconds { get; init; } = 5_000_000;

    public double RecoveryBackoffFactor { get; init; } = 2;

    public EffectStateMachineOptions Normalize()
    {
        long recoveryCooldown = Math.Clamp(RecoveryCooldownMicroseconds, 10_000, 30_000_000);
        long maximumRecoveryCooldown = Math.Max(
            recoveryCooldown,
            Math.Clamp(MaximumRecoveryCooldownMicroseconds, 10_000, 60_000_000));
        double recoveryBackoffFactor = double.IsFinite(RecoveryBackoffFactor)
            ? Math.Clamp(RecoveryBackoffFactor, 1, 8)
            : 2;

        return this with
        {
            HiddenLeaseMicroseconds = Math.Clamp(HiddenLeaseMicroseconds, 50_000, 5_000_000),
            ShowRetryMicroseconds = Math.Clamp(ShowRetryMicroseconds, 10_000, 1_000_000),
            RecoveryCooldownMicroseconds = recoveryCooldown,
            MaximumRecoveryCooldownMicroseconds = maximumRecoveryCooldown,
            RecoveryBackoffFactor = recoveryBackoffFactor,
        };
    }
}

public sealed record EffectTransition(
    EffectState State,
    IReadOnlyList<EffectCommand> Commands);

/// <summary>
/// Pure reducer plus a small stateful wrapper for the native-cursor replacement
/// protocol. Safety events always outrank visual transitions.
/// </summary>
public sealed class EffectStateMachine
{
    private readonly EffectStateMachineOptions _options;

    public EffectStateMachine(EffectStateMachineOptions? options = null)
    {
        _options = (options ?? EffectStateMachineOptions.Default).Normalize();
    }

    public EffectState State { get; private set; } = EffectState.Initial;

    public EffectTransition Dispatch(EffectEvent effectEvent)
    {
        var transition = Reduce(State, effectEvent, _options);
        State = transition.State;
        return transition;
    }

    public void Reset() => State = EffectState.Initial;

    public static EffectTransition Reduce(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedOptions = (options ?? EffectStateMachineOptions.Default).Normalize();

        if (effectEvent.TimestampMicroseconds < 0)
        {
            return NoChange(state);
        }

        if (IsSafetyEvent(effectEvent.Kind))
        {
            return HandleSafetyEvent(state, effectEvent, normalizedOptions);
        }

        return effectEvent.Kind switch
        {
            EffectEventKind.Trigger => HandleTrigger(state, effectEvent),
            EffectEventKind.OverlayReady => HandleOverlayReady(state, effectEvent, normalizedOptions),
            EffectEventKind.GuardArmedAcknowledged => HandleGuardArmed(state, effectEvent, normalizedOptions),
            EffectEventKind.HideAcknowledged => HandleHideAcknowledged(state, effectEvent, normalizedOptions),
            EffectEventKind.ReleaseRequested => HandleRelease(state, effectEvent),
            EffectEventKind.OverlayAtRest => HandleOverlayAtRest(state, effectEvent, normalizedOptions),
            EffectEventKind.ShowAcknowledged => HandleShowAcknowledged(state, effectEvent, normalizedOptions),
            EffectEventKind.LeaseRenewed => HandleLeaseRenewed(state, effectEvent, normalizedOptions),
            EffectEventKind.Tick => HandleTick(state, effectEvent, normalizedOptions),
            EffectEventKind.ResetFault => HandleResetFault(state),
            _ => NoChange(state),
        };
    }

    private static EffectTransition HandleTrigger(EffectState state, EffectEvent effectEvent)
    {
        if (state.Phase != EffectPhase.Idle
            || state.HighFidelityDisabled
            || state.RecoveryStatus is EffectRecoveryStatus.HalfOpen
                or EffectRecoveryStatus.PermanentlyDisabled
            || state.RecoveryStatus == EffectRecoveryStatus.CoolingDown
                && effectEvent.TimestampMicroseconds < state.RecoveryDeadlineMicroseconds
            || state.ShutdownRequested
            || effectEvent.Score is < 0 or > 1
            || !double.IsFinite(effectEvent.Score))
        {
            return NoChange(state);
        }

        var generation = checked(state.Generation + 1);
        var next = state with
        {
            Phase = EffectPhase.PreparingOverlay,
            Generation = generation,
            OverlayReady = false,
            GuardArmed = false,
            LeaseDeadlineMicroseconds = 0,
            NextShowRetryMicroseconds = 0,
            RecoveryStatus = state.RecoveryStatus == EffectRecoveryStatus.CoolingDown
                ? EffectRecoveryStatus.HalfOpen
                : EffectRecoveryStatus.Healthy,
            RecoveryDeadlineMicroseconds = 0,
        };

        return One(next, EffectCommandKind.PrepareOverlay);
    }

    private static EffectTransition HandleOverlayReady(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options)
    {
        if (!Matches(state, effectEvent) || state.Phase != EffectPhase.PreparingOverlay)
        {
            return NoChange(state);
        }

        if (!effectEvent.Success)
        {
            return FailBeforeHide(
                state,
                effectEvent,
                options,
                EffectFaultReason.OverlayPreparationFailed);
        }

        var next = state with
        {
            Phase = EffectPhase.ArmingGuard,
            OverlayReady = true,
        };
        return One(next, EffectCommandKind.ArmGuard);
    }

    private static EffectTransition HandleGuardArmed(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options)
    {
        if (!Matches(state, effectEvent) || state.Phase != EffectPhase.ArmingGuard)
        {
            return NoChange(state);
        }

        if (!effectEvent.Success)
        {
            return FailBeforeHide(
                state,
                effectEvent,
                options,
                EffectFaultReason.GuardArmFailed);
        }

        var next = state with
        {
            Phase = EffectPhase.AwaitingHideAcknowledgement,
            GuardArmed = true,
            NativeVisibility = NativeCursorVisibility.Unknown,
        };
        return One(next, EffectCommandKind.RequestHideNative);
    }

    private static EffectTransition HandleHideAcknowledged(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options)
    {
        if (!Matches(state, effectEvent)
            || state.Phase != EffectPhase.AwaitingHideAcknowledgement)
        {
            return NoChange(state);
        }

        if (!effectEvent.Success)
        {
            var faulted = ApplyFault(
                state,
                effectEvent,
                options,
                EffectFaultReason.HideFailed,
                FaultDisposition.Recoverable);
            return BeginCursorRestore(
                faulted,
                effectEvent.TimestampMicroseconds,
                options,
                becamePermanentlyDisabled: !state.HighFidelityDisabled && faulted.HighFidelityDisabled);
        }

        var next = state with
        {
            Phase = EffectPhase.NativeHidden,
            NativeVisibility = NativeCursorVisibility.KnownHidden,
            LeaseDeadlineMicroseconds = SaturatingAdd(
                effectEvent.TimestampMicroseconds,
                options.HiddenLeaseMicroseconds),
            RecoveryStatus = EffectRecoveryStatus.Healthy,
            RecoveryDeadlineMicroseconds = 0,
            ConsecutiveRecoverableFaults = 0,
            HighFidelityDisabled = false,
        };
        return One(next, EffectCommandKind.BeginGrow);
    }

    private static EffectTransition HandleRelease(EffectState state, EffectEvent effectEvent)
    {
        if (!Matches(state, effectEvent) || state.Phase != EffectPhase.NativeHidden)
        {
            return NoChange(state);
        }

        return One(state with { Phase = EffectPhase.Releasing }, EffectCommandKind.BeginRelease);
    }

    private static EffectTransition HandleOverlayAtRest(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options)
    {
        if (!Matches(state, effectEvent) || state.Phase != EffectPhase.Releasing)
        {
            return NoChange(state);
        }

        return BeginCursorRestore(state, effectEvent.TimestampMicroseconds, options);
    }

    private static EffectTransition HandleShowAcknowledged(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options)
    {
        if (!Matches(state, effectEvent)
            || state.Phase != EffectPhase.AwaitingShowAcknowledgement)
        {
            return NoChange(state);
        }

        if (!effectEvent.Success)
        {
            var retrying = state with
            {
                NativeVisibility = NativeCursorVisibility.Unknown,
                NextShowRetryMicroseconds = SaturatingAdd(
                    effectEvent.TimestampMicroseconds,
                    options.ShowRetryMicroseconds),
            };
            return NoChange(retrying);
        }

        var settledPhase = state.HighFidelityDisabled ? EffectPhase.Faulted : EffectPhase.Idle;
        var settled = state with
        {
            Phase = settledPhase,
            NativeVisibility = NativeCursorVisibility.KnownShown,
            OverlayReady = false,
            GuardArmed = false,
            LeaseDeadlineMicroseconds = 0,
            NextShowRetryMicroseconds = 0,
        };

        var commands = new List<EffectCommand>
        {
            new(EffectCommandKind.RemoveOverlay, state.Generation),
            // MagShowSystemCursor restores visibility but a stationary cursor
            // may not be painted until its owning window receives WM_SETCURSOR.
            // The overlay must be gone before that owner is resolved.
            new(EffectCommandKind.RefreshNativeCursor, state.Generation),
        };
        if (state.ShutdownRequested)
        {
            commands.Add(new EffectCommand(EffectCommandKind.CompleteShutdown, state.Generation));
        }

        return new EffectTransition(settled, commands);
    }

    private static EffectTransition HandleLeaseRenewed(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options)
    {
        if (!Matches(state, effectEvent)
            || state.Phase is not (EffectPhase.NativeHidden or EffectPhase.Releasing))
        {
            return NoChange(state);
        }

        return NoChange(state with
        {
            LeaseDeadlineMicroseconds = SaturatingAdd(
                effectEvent.TimestampMicroseconds,
                options.HiddenLeaseMicroseconds),
        });
    }

    private static EffectTransition HandleTick(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options)
    {
        if (state.Phase is EffectPhase.NativeHidden or EffectPhase.Releasing
            && effectEvent.TimestampMicroseconds >= state.LeaseDeadlineMicroseconds)
        {
            var leaseExpired = effectEvent with
            {
                Reason = EffectFaultReason.LeaseExpired,
                Disposition = FaultDisposition.Recoverable,
            };
            var faulted = ApplyFault(
                state,
                leaseExpired,
                options,
                EffectFaultReason.LeaseExpired,
                FaultDisposition.Recoverable);
            return BeginCursorRestore(
                faulted,
                effectEvent.TimestampMicroseconds,
                options,
                becamePermanentlyDisabled: !state.HighFidelityDisabled && faulted.HighFidelityDisabled);
        }

        if (state.Phase == EffectPhase.AwaitingShowAcknowledgement
            && effectEvent.TimestampMicroseconds >= state.NextShowRetryMicroseconds)
        {
            var retrying = state with
            {
                NextShowRetryMicroseconds = SaturatingAdd(
                    effectEvent.TimestampMicroseconds,
                    options.ShowRetryMicroseconds),
            };
            return One(retrying, EffectCommandKind.RequestShowNative);
        }

        return NoChange(state);
    }

    private static EffectTransition HandleResetFault(EffectState state)
    {
        if (state.Phase is not (EffectPhase.Idle or EffectPhase.Faulted)
            || state.NativeVisibility != NativeCursorVisibility.KnownShown
            || state.ShutdownRequested)
        {
            return NoChange(state);
        }

        return NoChange(state with
        {
            Phase = EffectPhase.Idle,
            HighFidelityDisabled = false,
            RecoveryStatus = EffectRecoveryStatus.Healthy,
            RecoveryDeadlineMicroseconds = 0,
            ConsecutiveRecoverableFaults = 0,
        });
    }

    private static EffectTransition HandleSafetyEvent(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options)
    {
        var shutdown = state.ShutdownRequested || effectEvent.Kind == EffectEventKind.Shutdown;
        var updated = state with { ShutdownRequested = shutdown };
        var faulted = ApplyFault(
            updated,
            effectEvent,
            options,
            DefaultReasonFor(effectEvent.Kind),
            DefaultDispositionFor(effectEvent.Kind));
        var becamePermanentlyDisabled = !state.HighFidelityDisabled && faulted.HighFidelityDisabled;

        if (state.Phase is EffectPhase.AwaitingHideAcknowledgement
            or EffectPhase.NativeHidden
            or EffectPhase.Releasing
            or EffectPhase.AwaitingShowAcknowledgement
            || state.NativeVisibility != NativeCursorVisibility.KnownShown)
        {
            return BeginCursorRestore(
                faulted,
                effectEvent.TimestampMicroseconds,
                options,
                becamePermanentlyDisabled);
        }

        var phase = faulted.HighFidelityDisabled ? EffectPhase.Faulted : EffectPhase.Idle;
        var settled = faulted with
        {
            Phase = phase,
            OverlayReady = false,
            GuardArmed = false,
            NativeVisibility = NativeCursorVisibility.KnownShown,
            LeaseDeadlineMicroseconds = 0,
            NextShowRetryMicroseconds = 0,
        };

        var commands = new List<EffectCommand>();
        if (state.Phase != EffectPhase.Idle || state.OverlayReady)
        {
            commands.Add(new EffectCommand(EffectCommandKind.RemoveOverlay, state.Generation));
        }

        if (becamePermanentlyDisabled)
        {
            commands.Add(new EffectCommand(EffectCommandKind.DisableHighFidelity, state.Generation));
        }

        if (shutdown)
        {
            commands.Add(new EffectCommand(EffectCommandKind.CompleteShutdown, state.Generation));
        }

        return new EffectTransition(settled, commands);
    }

    private static EffectTransition FailBeforeHide(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options,
        EffectFaultReason defaultReason)
    {
        var faulted = ApplyFault(
            state,
            effectEvent,
            options,
            defaultReason,
            FaultDisposition.Recoverable);
        var becamePermanentlyDisabled = !state.HighFidelityDisabled && faulted.HighFidelityDisabled;
        var settled = faulted with
        {
            Phase = faulted.HighFidelityDisabled ? EffectPhase.Faulted : EffectPhase.Idle,
            NativeVisibility = NativeCursorVisibility.KnownShown,
            OverlayReady = false,
            GuardArmed = false,
            LeaseDeadlineMicroseconds = 0,
            NextShowRetryMicroseconds = 0,
        };

        var commands = new List<EffectCommand>
        {
            new(EffectCommandKind.RemoveOverlay, state.Generation),
        };
        if (becamePermanentlyDisabled)
        {
            commands.Add(new EffectCommand(EffectCommandKind.DisableHighFidelity, state.Generation));
        }

        return new EffectTransition(settled, commands);
    }

    private static EffectTransition BeginCursorRestore(
        EffectState state,
        long timestampMicroseconds,
        EffectStateMachineOptions options,
        bool becamePermanentlyDisabled = false)
    {
        var recovering = state with
        {
            Phase = EffectPhase.AwaitingShowAcknowledgement,
            NativeVisibility = NativeCursorVisibility.Unknown,
            NextShowRetryMicroseconds = SaturatingAdd(
                timestampMicroseconds,
                options.ShowRetryMicroseconds),
        };

        var commands = new List<EffectCommand>
        {
            new(EffectCommandKind.RequestShowNative, state.Generation),
        };
        if (becamePermanentlyDisabled)
        {
            commands.Add(new EffectCommand(EffectCommandKind.DisableHighFidelity, state.Generation));
        }

        return new EffectTransition(recovering, commands);
    }

    private static EffectState ApplyFault(
        EffectState state,
        EffectEvent effectEvent,
        EffectStateMachineOptions options,
        EffectFaultReason defaultReason,
        FaultDisposition defaultDisposition)
    {
        var disposition = effectEvent.Kind == EffectEventKind.Disable
            ? FaultDisposition.Permanent
            : effectEvent.Disposition == FaultDisposition.Default
                ? defaultDisposition
                : effectEvent.Disposition;
        var reason = effectEvent.Reason == EffectFaultReason.None
            ? defaultReason
            : effectEvent.Reason;

        if (disposition == FaultDisposition.Default)
        {
            return state;
        }

        // A later recoverable symptom cannot downgrade an explicit permanent
        // disable. ResetFault is the only transition out of this state.
        if (state.RecoveryStatus == EffectRecoveryStatus.PermanentlyDisabled
            && disposition != FaultDisposition.Permanent)
        {
            return state;
        }

        if (disposition == FaultDisposition.Permanent)
        {
            return state with
            {
                HighFidelityDisabled = true,
                RecoveryStatus = EffectRecoveryStatus.PermanentlyDisabled,
                RecoveryDeadlineMicroseconds = 0,
                LastFaultReason = reason == EffectFaultReason.None
                    ? EffectFaultReason.Unspecified
                    : reason,
                LastFaultDisposition = disposition,
                LastFaultTimestampMicroseconds = effectEvent.TimestampMicroseconds,
            };
        }

        if (disposition == FaultDisposition.Recoverable)
        {
            int faultCount = state.ConsecutiveRecoverableFaults == int.MaxValue
                ? int.MaxValue
                : state.ConsecutiveRecoverableFaults + 1;
            long cooldown = GetRecoveryCooldown(options, faultCount);
            return state with
            {
                HighFidelityDisabled = false,
                RecoveryStatus = EffectRecoveryStatus.CoolingDown,
                RecoveryDeadlineMicroseconds = SaturatingAdd(
                    effectEvent.TimestampMicroseconds,
                    cooldown),
                ConsecutiveRecoverableFaults = faultCount,
                LastFaultReason = reason == EffectFaultReason.None
                    ? EffectFaultReason.Unspecified
                    : reason,
                LastFaultDisposition = disposition,
                LastFaultTimestampMicroseconds = effectEvent.TimestampMicroseconds,
            };
        }

        var recoveryStatus = state.RecoveryStatus;
        var recoveryDeadline = state.RecoveryDeadlineMicroseconds;
        if (recoveryStatus == EffectRecoveryStatus.HalfOpen)
        {
            // A desktop transition interrupted the probe. It is not a
            // component failure, but immediately probing again would race the
            // same transition, so return to one base cooldown.
            recoveryStatus = EffectRecoveryStatus.CoolingDown;
            recoveryDeadline = SaturatingAdd(
                effectEvent.TimestampMicroseconds,
                options.RecoveryCooldownMicroseconds);
        }

        return state with
        {
            RecoveryStatus = recoveryStatus,
            RecoveryDeadlineMicroseconds = recoveryDeadline,
            LastFaultReason = reason,
            LastFaultDisposition = disposition,
            LastFaultTimestampMicroseconds = effectEvent.TimestampMicroseconds,
        };
    }

    private static long GetRecoveryCooldown(
        EffectStateMachineOptions options,
        int faultCount)
    {
        double multiplier = Math.Pow(
            options.RecoveryBackoffFactor,
            Math.Clamp(faultCount - 1, 0, 62));
        double cooldown = options.RecoveryCooldownMicroseconds * multiplier;
        if (!double.IsFinite(cooldown)
            || cooldown >= options.MaximumRecoveryCooldownMicroseconds)
        {
            return options.MaximumRecoveryCooldownMicroseconds;
        }

        return Math.Max(options.RecoveryCooldownMicroseconds, (long)Math.Ceiling(cooldown));
    }

    private static bool Matches(EffectState state, EffectEvent effectEvent) =>
        effectEvent.Generation == state.Generation;

    private static bool IsSafetyEvent(EffectEventKind kind) => kind is
        EffectEventKind.OverlayFailed
        or EffectEventKind.Disable
        or EffectEventKind.Shutdown
        or EffectEventKind.SessionLock
        or EffectEventKind.Suspend
        or EffectEventKind.DisplayChanging
        or EffectEventKind.DeviceLost
        or EffectEventKind.HeartbeatLost
        or EffectEventKind.GuardExited
        or EffectEventKind.CursorInvalid;

    private static FaultDisposition DefaultDispositionFor(EffectEventKind kind) => kind switch
    {
        EffectEventKind.Disable => FaultDisposition.Permanent,
        EffectEventKind.OverlayFailed
            or EffectEventKind.DeviceLost
            or EffectEventKind.HeartbeatLost
            or EffectEventKind.GuardExited
            or EffectEventKind.CursorInvalid => FaultDisposition.Recoverable,
        _ => FaultDisposition.Transient,
    };

    private static EffectFaultReason DefaultReasonFor(EffectEventKind kind) => kind switch
    {
        EffectEventKind.OverlayFailed => EffectFaultReason.OverlayFailed,
        EffectEventKind.DeviceLost => EffectFaultReason.DeviceLost,
        EffectEventKind.HeartbeatLost => EffectFaultReason.HeartbeatLost,
        EffectEventKind.GuardExited => EffectFaultReason.GuardExited,
        EffectEventKind.CursorInvalid => EffectFaultReason.CursorInvalid,
        EffectEventKind.SessionLock => EffectFaultReason.SessionLocked,
        EffectEventKind.Suspend => EffectFaultReason.Suspended,
        EffectEventKind.DisplayChanging => EffectFaultReason.DisplayChanging,
        EffectEventKind.Disable => EffectFaultReason.UserDisabled,
        EffectEventKind.Shutdown => EffectFaultReason.Shutdown,
        _ => EffectFaultReason.Unspecified,
    };

    private static EffectTransition NoChange(EffectState state) => new(state, []);

    private static EffectTransition One(EffectState state, EffectCommandKind command) =>
        new(state, [new EffectCommand(command, state.Generation)]);

    private static long SaturatingAdd(long value, long delta) =>
        value > long.MaxValue - delta ? long.MaxValue : value + delta;
}
