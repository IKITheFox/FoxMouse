using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class EffectStateMachineTests
{
    [Fact]
    public void NormalSequenceHidesOnlyAfterOverlayAndGuardAreReady()
    {
        var machine = new EffectStateMachine();

        var trigger = machine.Dispatch(new EffectEvent(EffectEventKind.Trigger, 0, Score: 1));
        Assert.Equal(EffectPhase.PreparingOverlay, trigger.State.Phase);
        AssertCommand(trigger, EffectCommandKind.PrepareOverlay);
        Assert.DoesNotContain(trigger.Commands, static command => command.Kind == EffectCommandKind.RequestHideNative);

        var overlay = machine.Dispatch(new EffectEvent(EffectEventKind.OverlayReady, 1, 1));
        Assert.Equal(EffectPhase.ArmingGuard, overlay.State.Phase);
        AssertCommand(overlay, EffectCommandKind.ArmGuard);

        var guard = machine.Dispatch(new EffectEvent(EffectEventKind.GuardArmedAcknowledged, 2, 1));
        Assert.Equal(EffectPhase.AwaitingHideAcknowledgement, guard.State.Phase);
        Assert.Equal(NativeCursorVisibility.Unknown, guard.State.NativeVisibility);
        AssertCommand(guard, EffectCommandKind.RequestHideNative);

        var hidden = machine.Dispatch(new EffectEvent(EffectEventKind.HideAcknowledged, 3, 1));
        Assert.Equal(EffectPhase.NativeHidden, hidden.State.Phase);
        Assert.Equal(NativeCursorVisibility.KnownHidden, hidden.State.NativeVisibility);
        Assert.True(hidden.State.OverlayReady);
        Assert.True(hidden.State.GuardArmed);
        AssertCommand(hidden, EffectCommandKind.BeginGrow);
    }

    [Fact]
    public void NormalReleaseShowsBeforeRemovingOverlay()
    {
        var machine = DriveToHidden();

        var releasing = machine.Dispatch(new EffectEvent(EffectEventKind.ReleaseRequested, 10, 1));
        Assert.Equal(EffectPhase.Releasing, releasing.State.Phase);
        AssertCommand(releasing, EffectCommandKind.BeginRelease);

        var restoring = machine.Dispatch(new EffectEvent(EffectEventKind.OverlayAtRest, 20, 1));
        Assert.Equal(EffectPhase.AwaitingShowAcknowledgement, restoring.State.Phase);
        AssertCommand(restoring, EffectCommandKind.RequestShowNative);
        Assert.DoesNotContain(restoring.Commands, static command => command.Kind == EffectCommandKind.RemoveOverlay);
        Assert.DoesNotContain(restoring.Commands, static command => command.Kind == EffectCommandKind.RefreshNativeCursor);

        var shown = machine.Dispatch(new EffectEvent(EffectEventKind.ShowAcknowledged, 21, 1));
        Assert.Equal(EffectPhase.Idle, shown.State.Phase);
        Assert.Equal(NativeCursorVisibility.KnownShown, shown.State.NativeVisibility);
        Assert.Equal(
            [EffectCommandKind.RemoveOverlay, EffectCommandKind.RefreshNativeCursor],
            shown.Commands.Select(static command => command.Kind));
    }

    [Theory]
    [InlineData(EffectEventKind.OverlayFailed)]
    [InlineData(EffectEventKind.DeviceLost)]
    [InlineData(EffectEventKind.HeartbeatLost)]
    [InlineData(EffectEventKind.GuardExited)]
    [InlineData(EffectEventKind.CursorInvalid)]
    public void RecoverableSafetyEventWhileHiddenRequestsImmediateShowWithoutPermanentDisable(
        EffectEventKind eventKind)
    {
        var machine = DriveToHidden();

        var transition = machine.Dispatch(new EffectEvent(eventKind, 100, 1));

        Assert.Equal(EffectPhase.AwaitingShowAcknowledgement, transition.State.Phase);
        Assert.Equal(NativeCursorVisibility.Unknown, transition.State.NativeVisibility);
        Assert.False(transition.State.HighFidelityDisabled);
        Assert.Equal(EffectRecoveryStatus.CoolingDown, transition.State.RecoveryStatus);
        Assert.Equal(FaultDisposition.Recoverable, transition.State.LastFaultDisposition);
        Assert.True(transition.State.RecoveryDeadlineMicroseconds > 100);
        AssertCommand(transition, EffectCommandKind.RequestShowNative);
        Assert.DoesNotContain(
            transition.Commands,
            static command => command.Kind == EffectCommandKind.DisableHighFidelity);
    }

    [Fact]
    public void GuardExitRecoveryRefreshesOnlyAfterOverlayRemovalAndShowAcknowledgement()
    {
        var machine = DriveToHidden();

        EffectTransition recovery = machine.Dispatch(new EffectEvent(
            EffectEventKind.GuardExited,
            TimestampMicroseconds: 100,
            Generation: 1));

        Assert.Equal(EffectPhase.AwaitingShowAcknowledgement, recovery.State.Phase);
        Assert.Equal(
            [EffectCommandKind.RequestShowNative],
            recovery.Commands.Select(static command => command.Kind));

        EffectTransition restored = machine.Dispatch(new EffectEvent(
            EffectEventKind.ShowAcknowledged,
            TimestampMicroseconds: 101,
            Generation: 1,
            Success: true));

        Assert.Equal(NativeCursorVisibility.KnownShown, restored.State.NativeVisibility);
        Assert.Equal(
            [EffectCommandKind.RemoveOverlay, EffectCommandKind.RefreshNativeCursor],
            restored.Commands.Select(static command => command.Kind));
    }

    [Theory]
    [InlineData(EffectEventKind.SessionLock)]
    [InlineData(EffectEventKind.Suspend)]
    [InlineData(EffectEventKind.DisplayChanging)]
    public void TemporarySafetyEventRestoresWithoutPermanentlyDisabling(EffectEventKind eventKind)
    {
        var machine = DriveToHidden();

        var transition = machine.Dispatch(new EffectEvent(eventKind, 100, 1));
        var shown = machine.Dispatch(new EffectEvent(EffectEventKind.ShowAcknowledged, 101, 1));

        AssertCommand(transition, EffectCommandKind.RequestShowNative);
        Assert.False(shown.State.HighFidelityDisabled);
        Assert.Equal(EffectPhase.Idle, shown.State.Phase);
        Assert.Equal(EffectRecoveryStatus.Healthy, shown.State.RecoveryStatus);
    }

    [Fact]
    public void FaultBeforeHideDoesNotIssueUnnecessaryShow()
    {
        var machine = new EffectStateMachine();
        machine.Dispatch(new EffectEvent(EffectEventKind.Trigger, 0, Score: 1));

        var fault = machine.Dispatch(new EffectEvent(EffectEventKind.OverlayFailed, 1, 1));

        Assert.Equal(NativeCursorVisibility.KnownShown, fault.State.NativeVisibility);
        Assert.Equal(EffectPhase.Idle, fault.State.Phase);
        Assert.Equal(EffectRecoveryStatus.CoolingDown, fault.State.RecoveryStatus);
        Assert.False(fault.State.HighFidelityDisabled);
        Assert.DoesNotContain(fault.Commands, static command => command.Kind == EffectCommandKind.RequestShowNative);
        AssertCommand(fault, EffectCommandKind.RemoveOverlay);
    }

    [Fact]
    public void StaleAcknowledgementsAreIgnored()
    {
        var machine = new EffectStateMachine();
        machine.Dispatch(new EffectEvent(EffectEventKind.Trigger, 0, Score: 1));
        var before = machine.State;

        var transition = machine.Dispatch(new EffectEvent(EffectEventKind.OverlayReady, 1, 99));

        Assert.Equal(before, transition.State);
        Assert.Empty(transition.Commands);
    }

    [Fact]
    public void StaleReleaseCannotStopCurrentGeneration()
    {
        var machine = DriveToHidden();

        var transition = machine.Dispatch(new EffectEvent(EffectEventKind.ReleaseRequested, 10, 99));

        Assert.Equal(EffectPhase.NativeHidden, transition.State.Phase);
        Assert.Empty(transition.Commands);
    }

    [Fact]
    public void LeaseExpiryForcesRecovery()
    {
        var options = new EffectStateMachineOptions { HiddenLeaseMicroseconds = 50_000 };
        var machine = DriveToHidden(options);
        var deadline = machine.State.LeaseDeadlineMicroseconds;

        var transition = machine.Dispatch(new EffectEvent(EffectEventKind.Tick, deadline));

        Assert.Equal(EffectPhase.AwaitingShowAcknowledgement, transition.State.Phase);
        Assert.False(transition.State.HighFidelityDisabled);
        Assert.Equal(EffectRecoveryStatus.CoolingDown, transition.State.RecoveryStatus);
        Assert.Equal(EffectFaultReason.LeaseExpired, transition.State.LastFaultReason);
        AssertCommand(transition, EffectCommandKind.RequestShowNative);
    }

    [Fact]
    public void FailedShowIsRetriedOnTimer()
    {
        var options = new EffectStateMachineOptions { ShowRetryMicroseconds = 10_000 };
        var machine = DriveToHidden(options);
        machine.Dispatch(new EffectEvent(EffectEventKind.DeviceLost, 100, 1));
        var failure = machine.Dispatch(new EffectEvent(EffectEventKind.ShowAcknowledged, 101, 1, Success: false));

        var early = machine.Dispatch(new EffectEvent(EffectEventKind.Tick, failure.State.NextShowRetryMicroseconds - 1));
        var retry = machine.Dispatch(new EffectEvent(EffectEventKind.Tick, failure.State.NextShowRetryMicroseconds));

        Assert.Empty(early.Commands);
        AssertCommand(retry, EffectCommandKind.RequestShowNative);
    }

    [Fact]
    public void RecoverableFaultAutomaticallyHalfOpensAfterCooldownAndReturnsHealthy()
    {
        var options = new EffectStateMachineOptions
        {
            RecoveryCooldownMicroseconds = 10_000,
            MaximumRecoveryCooldownMicroseconds = 100_000,
        };
        var machine = DriveToHidden(options);
        machine.Dispatch(new EffectEvent(EffectEventKind.CursorInvalid, 100, 1));
        var shown = machine.Dispatch(new EffectEvent(EffectEventKind.ShowAcknowledged, 101, 1));
        long deadline = shown.State.RecoveryDeadlineMicroseconds;

        var early = machine.Dispatch(new EffectEvent(
            EffectEventKind.Trigger,
            deadline - 1,
            machine.State.Generation,
            Score: 1));
        Assert.Empty(early.Commands);
        Assert.Equal(EffectRecoveryStatus.CoolingDown, early.State.RecoveryStatus);

        var probe = machine.Dispatch(new EffectEvent(
            EffectEventKind.Trigger,
            deadline,
            machine.State.Generation,
            Score: 1));
        long generation = probe.State.Generation;
        Assert.Equal(EffectPhase.PreparingOverlay, probe.State.Phase);
        Assert.Equal(EffectRecoveryStatus.HalfOpen, probe.State.RecoveryStatus);
        AssertCommand(probe, EffectCommandKind.PrepareOverlay);

        machine.Dispatch(new EffectEvent(EffectEventKind.OverlayReady, deadline + 1, generation));
        machine.Dispatch(new EffectEvent(EffectEventKind.GuardArmedAcknowledged, deadline + 2, generation));
        var healthy = machine.Dispatch(new EffectEvent(
            EffectEventKind.HideAcknowledged,
            deadline + 3,
            generation));

        Assert.Equal(EffectPhase.NativeHidden, healthy.State.Phase);
        Assert.Equal(EffectRecoveryStatus.Healthy, healthy.State.RecoveryStatus);
        Assert.Equal(0, healthy.State.ConsecutiveRecoverableFaults);
        Assert.Equal(0, healthy.State.RecoveryDeadlineMicroseconds);
        AssertCommand(healthy, EffectCommandKind.BeginGrow);
    }

    [Fact]
    public void FailedHalfOpenProbeUsesBoundedExponentialBackoff()
    {
        var options = new EffectStateMachineOptions
        {
            RecoveryCooldownMicroseconds = 10_000,
            MaximumRecoveryCooldownMicroseconds = 25_000,
            RecoveryBackoffFactor = 2,
        };
        var machine = new EffectStateMachine(options);

        Assert.Equal(10_000, FailOverlayProbe(machine, 0));
        Assert.Equal(20_000, FailOverlayProbe(machine, machine.State.RecoveryDeadlineMicroseconds));
        Assert.Equal(25_000, FailOverlayProbe(machine, machine.State.RecoveryDeadlineMicroseconds));
        Assert.Equal(3, machine.State.ConsecutiveRecoverableFaults);
        Assert.False(machine.State.HighFidelityDisabled);
    }

    [Fact]
    public void ExplicitPermanentFaultDisablesOnlyAfterRequestingNativeCursorRestore()
    {
        var machine = DriveToHidden();

        var recovery = machine.Dispatch(new EffectEvent(
            EffectEventKind.OverlayFailed,
            100,
            1,
            Reason: EffectFaultReason.OverlayFailed,
            Disposition: FaultDisposition.Permanent));

        Assert.Equal(EffectPhase.AwaitingShowAcknowledgement, recovery.State.Phase);
        Assert.True(recovery.State.HighFidelityDisabled);
        Assert.Equal(EffectRecoveryStatus.PermanentlyDisabled, recovery.State.RecoveryStatus);
        AssertCommand(recovery, EffectCommandKind.RequestShowNative);
        AssertCommand(recovery, EffectCommandKind.DisableHighFidelity);
        Assert.DoesNotContain(
            recovery.Commands,
            static command => command.Kind == EffectCommandKind.RemoveOverlay);

        var shown = machine.Dispatch(new EffectEvent(EffectEventKind.ShowAcknowledged, 101, 1));
        Assert.Equal(EffectPhase.Faulted, shown.State.Phase);
        Assert.Equal(NativeCursorVisibility.KnownShown, shown.State.NativeVisibility);
        AssertCommand(shown, EffectCommandKind.RemoveOverlay);
    }

    [Fact]
    public void UserDisableIsPermanentEvenWhenCallerRequestsRecoverableDisposition()
    {
        var machine = new EffectStateMachine();

        var disabled = machine.Dispatch(new EffectEvent(
            EffectEventKind.Disable,
            10,
            Disposition: FaultDisposition.Recoverable));

        Assert.Equal(EffectPhase.Faulted, disabled.State.Phase);
        Assert.True(disabled.State.HighFidelityDisabled);
        Assert.Equal(EffectRecoveryStatus.PermanentlyDisabled, disabled.State.RecoveryStatus);
        Assert.Equal(EffectFaultReason.UserDisabled, disabled.State.LastFaultReason);
        AssertCommand(disabled, EffectCommandKind.DisableHighFidelity);
    }

    [Theory]
    [InlineData(EffectEventKind.DeviceLost)]
    [InlineData(EffectEventKind.HeartbeatLost)]
    [InlineData(EffectEventKind.GuardExited)]
    [InlineData(EffectEventKind.CursorInvalid)]
    [InlineData(EffectEventKind.OverlayFailed)]
    public void HealthResetClearsRecoverableCooldown(EffectEventKind eventKind)
    {
        var machine = new EffectStateMachine();
        machine.Dispatch(new EffectEvent(eventKind, 10));
        Assert.Equal(EffectRecoveryStatus.CoolingDown, machine.State.RecoveryStatus);

        var reset = machine.Dispatch(new EffectEvent(EffectEventKind.ResetFault, 11));

        Assert.Equal(EffectPhase.Idle, reset.State.Phase);
        Assert.Equal(EffectRecoveryStatus.Healthy, reset.State.RecoveryStatus);
        Assert.Equal(0, reset.State.ConsecutiveRecoverableFaults);
        Assert.Equal(0, reset.State.RecoveryDeadlineMicroseconds);
        Assert.False(reset.State.HighFidelityDisabled);
    }

    [Fact]
    public void HealthResetCanRecoverExplicitPermanentFaultWhenNativeCursorIsKnownShown()
    {
        var machine = new EffectStateMachine();
        machine.Dispatch(new EffectEvent(EffectEventKind.Disable, 10));

        var reset = machine.Dispatch(new EffectEvent(EffectEventKind.ResetFault, 11));

        Assert.Equal(EffectPhase.Idle, reset.State.Phase);
        Assert.Equal(EffectRecoveryStatus.Healthy, reset.State.RecoveryStatus);
        Assert.False(reset.State.HighFidelityDisabled);
    }

    [Theory]
    [InlineData(EffectEventKind.SessionLock)]
    [InlineData(EffectEventKind.Suspend)]
    [InlineData(EffectEventKind.DisplayChanging)]
    public void TransientDesktopEventsDoNotIncreaseRecoverableFaultCount(EffectEventKind eventKind)
    {
        var machine = new EffectStateMachine();

        var transition = machine.Dispatch(new EffectEvent(eventKind, 10));

        Assert.Equal(EffectRecoveryStatus.Healthy, transition.State.RecoveryStatus);
        Assert.Equal(0, transition.State.ConsecutiveRecoverableFaults);
        Assert.Equal(FaultDisposition.Transient, transition.State.LastFaultDisposition);
        Assert.False(transition.State.HighFidelityDisabled);
    }

    [Theory]
    [InlineData(EffectEventKind.OverlayReady, EffectFaultReason.OverlayPreparationFailed)]
    [InlineData(EffectEventKind.GuardArmedAcknowledged, EffectFaultReason.GuardArmFailed)]
    public void PreparationFailuresAreRecoverable(
        EffectEventKind failingEvent,
        EffectFaultReason expectedReason)
    {
        var machine = new EffectStateMachine();
        machine.Dispatch(new EffectEvent(EffectEventKind.Trigger, 0, Score: 1));
        if (failingEvent == EffectEventKind.GuardArmedAcknowledged)
        {
            machine.Dispatch(new EffectEvent(EffectEventKind.OverlayReady, 1, 1));
        }

        var failure = machine.Dispatch(new EffectEvent(
            failingEvent,
            2,
            1,
            Success: false));

        Assert.Equal(EffectPhase.Idle, failure.State.Phase);
        Assert.Equal(NativeCursorVisibility.KnownShown, failure.State.NativeVisibility);
        Assert.Equal(EffectRecoveryStatus.CoolingDown, failure.State.RecoveryStatus);
        Assert.Equal(expectedReason, failure.State.LastFaultReason);
        Assert.False(failure.State.HighFidelityDisabled);
        AssertCommand(failure, EffectCommandKind.RemoveOverlay);
        Assert.DoesNotContain(
            failure.Commands,
            static command => command.Kind == EffectCommandKind.DisableHighFidelity);
    }

    [Fact]
    public void FailureReasonAndExplicitDispositionArePropagated()
    {
        var machine = new EffectStateMachine();
        machine.Dispatch(new EffectEvent(EffectEventKind.Trigger, 0, Score: 1));

        var failure = machine.Dispatch(new EffectEvent(
            EffectEventKind.OverlayReady,
            1,
            1,
            Success: false,
            Reason: EffectFaultReason.Unspecified,
            Disposition: FaultDisposition.Permanent));

        Assert.Equal(EffectFaultReason.Unspecified, failure.State.LastFaultReason);
        Assert.Equal(FaultDisposition.Permanent, failure.State.LastFaultDisposition);
        Assert.Equal(EffectRecoveryStatus.PermanentlyDisabled, failure.State.RecoveryStatus);
    }

    [Fact]
    public void ShutdownCompletesOnlyAfterCursorIsShown()
    {
        var machine = DriveToHidden();

        var shutdown = machine.Dispatch(new EffectEvent(EffectEventKind.Shutdown, 100, 1));
        Assert.DoesNotContain(shutdown.Commands, static command => command.Kind == EffectCommandKind.CompleteShutdown);

        var shown = machine.Dispatch(new EffectEvent(EffectEventKind.ShowAcknowledged, 101, 1));
        Assert.Equal(
            [
                EffectCommandKind.RemoveOverlay,
                EffectCommandKind.RefreshNativeCursor,
                EffectCommandKind.CompleteShutdown,
            ],
            shown.Commands.Select(static command => command.Kind));
        Assert.Equal(NativeCursorVisibility.KnownShown, shown.State.NativeVisibility);
    }

    [Fact]
    public void RandomEventSequencesPreserveCoreSafetyInvariants()
    {
        var eventKinds = Enum.GetValues<EffectEventKind>();
        for (var seed = 0; seed < 100; seed++)
        {
            var random = new Random(seed);
            var machine = new EffectStateMachine();
            var timestamp = 0L;
            for (var index = 0; index < 1_000; index++)
            {
                timestamp += random.Next(1, 20_000);
                var kind = eventKinds[random.Next(eventKinds.Length)];
                var generation = random.Next(4) == 0
                    ? machine.State.Generation + 1
                    : machine.State.Generation;
                var transition = machine.Dispatch(new EffectEvent(
                    kind,
                    timestamp,
                    generation,
                    random.Next(5) != 0,
                    random.NextDouble()));

                AssertInvariants(transition.State);
            }
        }
    }

    [Fact]
    public void RandomRecoverableFaultCyclesNeverLatchHighFidelityOff()
    {
        var recoverableKinds = new[]
        {
            EffectEventKind.OverlayFailed,
            EffectEventKind.DeviceLost,
            EffectEventKind.HeartbeatLost,
            EffectEventKind.GuardExited,
            EffectEventKind.CursorInvalid,
        };
        var options = new EffectStateMachineOptions
        {
            RecoveryCooldownMicroseconds = 10_000,
            MaximumRecoveryCooldownMicroseconds = 80_000,
        };

        for (var seed = 0; seed < 25; seed++)
        {
            var random = new Random(seed);
            var machine = new EffectStateMachine(options);
            var timestamp = 0L;
            for (var cycle = 0; cycle < 100; cycle++)
            {
                timestamp = Math.Max(timestamp + 1, machine.State.RecoveryDeadlineMicroseconds);
                var trigger = machine.Dispatch(new EffectEvent(
                    EffectEventKind.Trigger,
                    timestamp++,
                    machine.State.Generation,
                    Score: 1));
                AssertCommand(trigger, EffectCommandKind.PrepareOverlay);
                long generation = trigger.State.Generation;

                int faultStage = random.Next(4);
                if (faultStage >= 1)
                {
                    machine.Dispatch(new EffectEvent(
                        EffectEventKind.OverlayReady,
                        timestamp++,
                        generation));
                }

                if (faultStage >= 2)
                {
                    machine.Dispatch(new EffectEvent(
                        EffectEventKind.GuardArmedAcknowledged,
                        timestamp++,
                        generation));
                }

                if (faultStage >= 3)
                {
                    machine.Dispatch(new EffectEvent(
                        EffectEventKind.HideAcknowledged,
                        timestamp++,
                        generation));
                }

                var fault = machine.Dispatch(new EffectEvent(
                    recoverableKinds[random.Next(recoverableKinds.Length)],
                    timestamp++,
                    generation));
                Assert.False(fault.State.HighFidelityDisabled);
                Assert.Equal(EffectRecoveryStatus.CoolingDown, fault.State.RecoveryStatus);
                Assert.DoesNotContain(
                    fault.Commands,
                    static command => command.Kind == EffectCommandKind.DisableHighFidelity);

                if (fault.State.NativeVisibility != NativeCursorVisibility.KnownShown)
                {
                    AssertCommand(fault, EffectCommandKind.RequestShowNative);
                    var shown = machine.Dispatch(new EffectEvent(
                        EffectEventKind.ShowAcknowledged,
                        timestamp++,
                        generation));
                    Assert.Equal(NativeCursorVisibility.KnownShown, shown.State.NativeVisibility);
                }

                Assert.Equal(EffectPhase.Idle, machine.State.Phase);
                Assert.Equal(EffectRecoveryStatus.CoolingDown, machine.State.RecoveryStatus);
                Assert.False(machine.State.HighFidelityDisabled);
                AssertInvariants(machine.State);
            }
        }
    }

    private static EffectStateMachine DriveToHidden(EffectStateMachineOptions? options = null)
    {
        var machine = new EffectStateMachine(options);
        machine.Dispatch(new EffectEvent(EffectEventKind.Trigger, 0, Score: 1));
        machine.Dispatch(new EffectEvent(EffectEventKind.OverlayReady, 1, 1));
        machine.Dispatch(new EffectEvent(EffectEventKind.GuardArmedAcknowledged, 2, 1));
        machine.Dispatch(new EffectEvent(EffectEventKind.HideAcknowledged, 3, 1));
        Assert.Equal(EffectPhase.NativeHidden, machine.State.Phase);
        return machine;
    }

    private static long FailOverlayProbe(EffectStateMachine machine, long timestamp)
    {
        var trigger = machine.Dispatch(new EffectEvent(
            EffectEventKind.Trigger,
            timestamp,
            machine.State.Generation,
            Score: 1));
        AssertCommand(trigger, EffectCommandKind.PrepareOverlay);

        var failed = machine.Dispatch(new EffectEvent(
            EffectEventKind.OverlayReady,
            timestamp + 1,
            trigger.State.Generation,
            Success: false));
        Assert.Equal(EffectRecoveryStatus.CoolingDown, failed.State.RecoveryStatus);
        return failed.State.RecoveryDeadlineMicroseconds - (timestamp + 1);
    }

    private static void AssertCommand(EffectTransition transition, EffectCommandKind commandKind) =>
        Assert.Contains(transition.Commands, command => command.Kind == commandKind);

    private static void AssertInvariants(EffectState state)
    {
        if (state.Phase == EffectPhase.NativeHidden)
        {
            Assert.True(state.OverlayReady);
            Assert.True(state.GuardArmed);
            Assert.Equal(NativeCursorVisibility.KnownHidden, state.NativeVisibility);
            Assert.True(state.LeaseDeadlineMicroseconds > 0);
        }

        if (state.Phase is EffectPhase.Idle or EffectPhase.Faulted)
        {
            Assert.Equal(NativeCursorVisibility.KnownShown, state.NativeVisibility);
        }

        if (state.Phase == EffectPhase.Faulted)
        {
            Assert.True(state.HighFidelityDisabled);
            Assert.Equal(EffectRecoveryStatus.PermanentlyDisabled, state.RecoveryStatus);
        }

        if (state.HighFidelityDisabled)
        {
            Assert.Equal(EffectRecoveryStatus.PermanentlyDisabled, state.RecoveryStatus);
        }

        if (state.RecoveryStatus == EffectRecoveryStatus.PermanentlyDisabled)
        {
            Assert.True(state.HighFidelityDisabled);
            Assert.Equal(0, state.RecoveryDeadlineMicroseconds);
        }

        if (state.RecoveryStatus == EffectRecoveryStatus.CoolingDown)
        {
            Assert.False(state.HighFidelityDisabled);
            Assert.True(state.RecoveryDeadlineMicroseconds > 0);
            Assert.True(state.ConsecutiveRecoverableFaults > 0);
        }

        if (state.RecoveryStatus == EffectRecoveryStatus.HalfOpen)
        {
            Assert.False(state.HighFidelityDisabled);
            Assert.Contains(
                state.Phase,
                new[]
                {
                    EffectPhase.PreparingOverlay,
                    EffectPhase.ArmingGuard,
                    EffectPhase.AwaitingHideAcknowledgement,
                });
        }

        if (state.NativeVisibility == NativeCursorVisibility.KnownHidden)
        {
            Assert.Contains(state.Phase, new[] { EffectPhase.NativeHidden, EffectPhase.Releasing });
        }
    }
}
