using FoxMouse.Core;
using System.Drawing;
using FoxMouse.Platform.Windows.Cursor;
using FoxMouse.Platform.Windows.Diagnostics;
using FoxMouse.Platform.Windows.Input;
using FoxMouse.Platform.Windows.Policy;
using FoxMouse.Platform.Windows.Rendering;
using FoxMouse.Platform.Windows.Visibility;

namespace FoxMouse.App;

internal sealed class FoxMouseEngine : IAsyncDisposable
{
    private const int GuardLeaseMilliseconds = 1_200;
    private const int GuardSupervisorIntervalMilliseconds = 250;
    private const long MinimumAcceptedEffectMicroseconds = 300_000;
    private const long TriggerRetryIntervalMicroseconds = 100_000;
    private const long MaximumRenderHeartbeatAgeMicroseconds = 500_000;
    private const int CursorObservationGraceFrames = 3;
    private const long CursorObservationGraceMicroseconds = 80_000;
    private const long RuntimePolicyRecheckIntervalMicroseconds = 50_000;

    private readonly RawInputSink _input = new();
    private readonly InputNormalizer _normalizer = new();
    private readonly CursorTracker _cursorTracker = new();
    private readonly LayeredCursorOverlay _overlay = new();
    private readonly InteractionPolicy _policy = new();
    private readonly ISystemCursorRefresher _cursorRefresher = new SystemCursorRefresher();
    private readonly RollingFileLogger _logger;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly System.Windows.Forms.Timer _cursorRecoveryTimer;
    private long _cursorRecoveryGeneration;
    private int _cursorRecoveryAttempt;
    private Point _cursorRecoveryPosition;
    private readonly SynchronizationContext _uiContext;
    private readonly int _uiThreadId;
    private readonly CancellationTokenSource _safetyOperationCancellation = new();
    private readonly SemaphoreSlim _guardLifecycleGate = new(1, 1);
    private readonly object _disposeGate = new();
    private volatile GuardClient _guard = new();
    private volatile GuardClient? _armedGuard;
    private EffectStateMachine _safetyMachine;
    private ShakeDetector _detector;
    private ScaleAnimator _animator;
    private volatile FoxMouseSettings _settings;
    private HighResolutionTimerLease? _timerResolution;
    private EffectKind _effectKind;
    private double _lastDetectorScore;
    private long _previewUntilMicroseconds;
    private long _lastRuntimePolicyCheckMicroseconds;
    private long _lastTriggerAttemptMicroseconds;
    private long _effectVisibleUntilMicroseconds;
    private long _firstCursorObservationFailureMicroseconds;
    private long _nextGuardProbeMicroseconds;
    private long _lastLoggedFaultTimestampMicroseconds = -1;
    private long _activeGeneration;
    private long _armedGuardGeneration;
    private long _lastSuccessfulRenderHeartbeatMicroseconds = -1;
    private long _renderHeartbeatGeneration;
    private long _pendingHideGeneration;
    private long _pendingHideTimestampMicroseconds = -1;
    private int _replacementHeartbeatActive;
    private int _pendingHideAcknowledgement;
    private Task? _hideTask;
    private Task? _showTask;
    private Task? _guardSupervisorTask;
    private Task? _disposeTask;
    private CursorObservation _lastValidCursor;
    private CursorRenderQuality _activeCursorQuality;
    private double _acceptedTriggerScore;
    private int _cursorObservationFailureFrames;
    private int _guardProbeFailures;
    private volatile bool _guardReady;
    private volatile bool _hideInFlight;
    private volatile bool _showInFlight;
    private volatile bool _shuttingDown;
    private volatile bool _disposed;
    private bool _cursorInvalidationPending;
    private bool _hasLastValidCursor;

    public FoxMouseEngine(FoxMouseSettings settings, RollingFileLogger logger)
    {
        _settings = SettingsNormalizer.Normalize(settings);
        _logger = logger;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _uiThreadId = Environment.CurrentManagedThreadId;
        _safetyMachine = CreateSafetyMachine();
        _detector = new ShakeDetector(ShakeDetectorOptions.ForSensitivity(_settings.Sensitivity));
        _animator = CreateAnimator();
        _renderTimer = new System.Windows.Forms.Timer { Interval = 8 };
        _renderTimer.Tick += HandleRenderTick;
        _cursorRecoveryTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _cursorRecoveryTimer.Tick += HandleCursorRecoveryTick;
        _overlay.DpiChanged += HandleOverlayDpiChanged;
        _input.PacketReceived += HandleRawPacket;
        _input.DeviceTopologyChanged += HandleDeviceTopologyChanged;
        _guard.GuardLost += HandleGuardLost;
    }

    public string StatusText => _effectKind switch
    {
        EffectKind.HighFidelity => FoxMouse.Core.UiText.Get("NativeActive"),
        EffectKind.Compatibility => FoxMouse.Core.UiText.Get("RingActive"),
        _ when !_settings.Enabled => FoxMouse.Core.UiText.Get("Paused"),
        _ when _settings.Mode == CursorEffectMode.Compatibility => FoxMouse.Core.UiText.Get("RingReady"),
        _ when _safetyMachine.State.RecoveryStatus == EffectRecoveryStatus.CoolingDown => FoxMouse.Core.UiText.Get("NativeRecovering"),
        _ when !_guardReady || _safetyMachine.State.HighFidelityDisabled => FoxMouse.Core.UiText.Get("NativeUnavailable"),
        _ => FoxMouse.Core.UiText.Get("Enabled"),
    };

    internal bool IsGuardReady => _guardReady && _guard.IsConnected;

    internal bool IsNativeCursorHandoffComplete => CanRefreshNativeCursor(_safetyMachine.State.Generation);

    internal bool IsHighFidelityReplacementActive =>
        _effectKind == EffectKind.HighFidelity &&
        _safetyMachine.State.Phase == EffectPhase.NativeHidden &&
        _guard.IsHidden;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_shuttingDown || _disposed)
        {
            return;
        }

        _guardSupervisorTask ??= RunGuardSupervisorAsync(_safetyOperationCancellation.Token);

        string? guardPath = FindGuardExecutable();
        if (guardPath is null)
        {
            _logger.Warning("guard-not-found", "High-fidelity mode will use the locator fallback.");
            return;
        }

        await _guardLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_shuttingDown || _disposed)
            {
                return;
            }

            _guardReady = await _guard.StartAsync(guardPath, cancellationToken);
            _logger.Information(_guardReady ? "guard-ready" : "guard-unavailable");
            ResetHighFidelityFaultIfSafe();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Error("guard-start-failed", exception);
            _guardReady = false;
        }
        finally
        {
            _guardLifecycleGate.Release();
        }
    }

    public async Task ApplySettingsAsync(
        FoxMouseSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (_shuttingDown || _disposed)
        {
            return;
        }

        FoxMouseSettings normalized = SettingsNormalizer.Normalize(settings);
        CursorEffectMode previousMode = _settings.Mode;
        if (_effectKind != EffectKind.None)
        {
            HandleSafetyEvent(EffectEventKind.DisplayChanging);
        }

        bool wasEnabled = _settings.Enabled;
        _settings = normalized;
        _detector = new ShakeDetector(ShakeDetectorOptions.ForSensitivity(_settings.Sensitivity));
        _animator = CreateAnimator();
        _normalizer.Reset();
        _policy.ResetPointerState();
        if (!_settings.Enabled)
        {
            HandleSafetyEvent(EffectEventKind.Disable);
            await AwaitTaskSafelyAsync(_showTask);
        }
        else if ((!wasEnabled || previousMode != CursorEffectMode.HighFidelity) &&
                 _settings.Mode == CursorEffectMode.HighFidelity)
        {
            await AwaitTaskSafelyAsync(_showTask);
            bool guardReady = await ReprobeGuardAsync(cancellationToken);
            if (guardReady)
            {
                ResetHighFidelityFaultIfSafe();
            }
        }

        _logger.Information("settings-applied", $"mode={_settings.Mode}; sensitivity={_settings.Sensitivity:F2}; maxScale={_settings.MaxScale:F2}");
    }

    public void Preview()
    {
        try
        {
            PreviewCore();
        }
        catch (Exception exception)
        {
            RecoverFromEngineCallbackFailure("preview-processing-failed", exception);
        }
    }

    private void PreviewCore()
    {
        if (_shuttingDown || _disposed || !_settings.Enabled)
        {
            return;
        }

        long now = _input.CurrentTimestampMicroseconds;
        _previewUntilMicroseconds = now + 520_000;
        if (_effectKind == EffectKind.None)
        {
            BeginEffect(now, 1.0);
        }
    }

    public void HandleSafetyEvent(EffectEventKind kind)
    {
        if (_shuttingDown || _disposed)
        {
            return;
        }

        long now = _input.CurrentTimestampMicroseconds;
        _previewUntilMicroseconds = 0;
        _lastDetectorScore = 0;
        _detector.ResetAll();
        _normalizer.Reset();

        if (_effectKind == EffectKind.Compatibility)
        {
            StopVisualEffect();
            return;
        }

        Execute(_safetyMachine.Dispatch(new EffectEvent(
            kind,
            now,
            _safetyMachine.State.Generation)));
    }

    public void InvalidateCursorForDisplayChange()
    {
        if (_shuttingDown || _disposed)
        {
            return;
        }

        _cursorInvalidationPending = true;
        HandleSafetyEvent(EffectEventKind.DisplayChanging);
        ApplyPendingCursorInvalidationIfSafe();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _shuttingDown = true;
        TryEngineCleanup(() => _input.PacketReceived -= HandleRawPacket, "input-unsubscribe-failed");
        TryEngineCleanup(() => _input.DeviceTopologyChanged -= HandleDeviceTopologyChanged, "device-unsubscribe-failed");
        TryEngineCleanup(() => _guard.GuardLost -= HandleGuardLost, "guard-unsubscribe-failed");
        TryEngineCleanup(_renderTimer.Stop, "render-timer-stop-failed");
        TryEngineCleanup(() => _renderTimer.Tick -= HandleRenderTick, "render-timer-unsubscribe-failed");
        TryEngineCleanup(() => _overlay.DpiChanged -= HandleOverlayDpiChanged, "overlay-dpi-unsubscribe-failed");
        TryEngineCleanup(() => _timerResolution?.Dispose(), "timer-resolution-release-failed");
        _timerResolution = null;
        TryEngineCleanup(_safetyOperationCancellation.Cancel, "safety-operation-cancel-failed");
        TryEngineCleanup(_input.Dispose, "input-dispose-failed");

        bool restored = false;
        try
        {
            await AwaitSafetyOperationsAsync();
            await _guardLifecycleGate.WaitAsync();
            try
            {
                restored = await RestoreNativeCursorForShutdownAsync();
                await _guard.DisposeAsync();
                restored |= !_guard.IsHidden;
            }
            finally
            {
                _guardLifecycleGate.Release();
            }
        }
        catch (Exception exception)
        {
            _logger.Error("engine-shutdown-failed", exception);
        }
        finally
        {
            if (!restored)
            {
                _logger.Warning("cursor-restore-unconfirmed", "Guard recovery remains responsible for restoring the native cursor.");
            }

            TryEngineCleanup(_overlay.HideOverlay, "overlay-hide-during-dispose-failed");
            if ((restored || !_guard.IsHidden) && !_overlay.IsVisible)
            {
                TryRefreshNativeCursor("cursor-refresh-during-dispose-failed");
            }
            TryEngineCleanup(_overlay.Dispose, "overlay-dispose-failed");
            TryEngineCleanup(_cursorTracker.Dispose, "cursor-tracker-dispose-failed");
            TryEngineCleanup(_renderTimer.Dispose, "render-timer-dispose-failed");
            TryEngineCleanup(_cursorRecoveryTimer.Dispose, "cursor-recovery-timer-dispose-failed");
            TryEngineCleanup(_safetyOperationCancellation.Dispose, "safety-cancellation-dispose-failed");
            TryEngineCleanup(_guardLifecycleGate.Dispose, "guard-gate-dispose-failed");
            _disposed = true;
        }
    }

    private void HandleRawPacket(object? sender, RawMousePacket packet)
    {
        try
        {
            HandleRawPacketCore(packet);
        }
        catch (Exception exception)
        {
            // Raw Input can run at several thousand packets per second. Keep
            // this boundary allocation-free while sharing one recovery path.
            RecoverFromEngineCallbackFailure("raw-input-processing-failed", exception);
        }
    }

    private void HandleRawPacketCore(RawMousePacket packet)
    {
        if (_shuttingDown || _disposed || !_settings.Enabled)
        {
            return;
        }

        MotionSample sample = _normalizer.Normalize(packet);
        _policy.ObservePointer(sample);
        ShakeDetectionResult result = _detector.Process(sample);
        _lastDetectorScore = result.IsActive ? result.Score : 0;
        if (result.IsActive &&
            _effectKind == EffectKind.None &&
            (sample.TimestampMicroseconds < _lastTriggerAttemptMicroseconds ||
             sample.TimestampMicroseconds - _lastTriggerAttemptMicroseconds >= TriggerRetryIntervalMicroseconds))
        {
            _lastTriggerAttemptMicroseconds = sample.TimestampMicroseconds;
            BeginEffect(sample.TimestampMicroseconds, result.Score);
        }
        else if (result.BecameInactive &&
                 _effectKind == EffectKind.HighFidelity &&
                 _safetyMachine.State.Phase == EffectPhase.NativeHidden &&
                 ShouldRequestRelease(
                     sample.TimestampMicroseconds,
                     _effectVisibleUntilMicroseconds,
                     detectorScore: 0))
        {
            Execute(_safetyMachine.Dispatch(new EffectEvent(
                EffectEventKind.ReleaseRequested,
                sample.TimestampMicroseconds,
                _safetyMachine.State.Generation)));
        }
    }

    private void BeginEffect(long timestampMicroseconds, double score)
    {
        TriggerBlockReason blocked = _policy.Evaluate(
            _settings.DisableWhileDragging,
            _settings.PauseInFullscreen,
            _settings.ExcludedProcesses);
        if (blocked != TriggerBlockReason.None)
        {
            _logger.Information("trigger-suppressed", blocked.ToString());
            return;
        }

        _lastRuntimePolicyCheckMicroseconds = timestampMicroseconds;

        if (!_cursorTracker.TryObserve(
                out CursorObservation cursor,
                requestedMaximumScale: _settings.MaxScale) ||
            !cursor.IsVisible)
        {
            _logger.Warning("cursor-unavailable");
            return;
        }

        if (_settings.Mode == CursorEffectMode.Compatibility)
        {
            BeginCompatibilityEffect(timestampMicroseconds, score);
            return;
        }

        EffectState safetyState = _safetyMachine.State;
        bool recoveryAllowsAttempt = safetyState.RecoveryStatus switch
        {
            EffectRecoveryStatus.Healthy => true,
            EffectRecoveryStatus.CoolingDown => timestampMicroseconds >= safetyState.RecoveryDeadlineMicroseconds,
            _ => false,
        };
        bool guardAvailable = TryGetAvailableGuard(out _);
        if (!recoveryAllowsAttempt ||
            safetyState.HighFidelityDisabled ||
            !guardAvailable ||
            cursor.Image is not { SupportsReplacement: true })
        {
            string reason = !recoveryAllowsAttempt
                ? $"recovery-{safetyState.RecoveryStatus.ToString().ToLowerInvariant()}"
                : safetyState.HighFidelityDisabled
                    ? "permanently-disabled"
                : !guardAvailable
                    ? "guard-unavailable"
                    : cursor.Image is null
                        ? "cursor-image-unavailable"
                        : "cursor-raster-unsupported";
            _logger.Warning("high-fidelity-trigger-suppressed", reason);
            return;
        }

        _animator.Reset();
        _acceptedTriggerScore = Math.Max(score, 0.30);
        _ = _animator.Step(timestampMicroseconds, _acceptedTriggerScore);
        EffectTransition transition = _safetyMachine.Dispatch(new EffectEvent(
            EffectEventKind.Trigger,
            timestampMicroseconds,
            _safetyMachine.State.Generation,
            Score: score));
        if (transition.State.Phase != EffectPhase.PreparingOverlay)
        {
            _animator.Reset();
            return;
        }

        _activeCursorQuality = cursor.Image.RenderQuality;
        RememberValidCursor(cursor);
        _effectKind = EffectKind.HighFidelity;
        StartRenderLoop();
        Execute(transition);
    }

    private void BeginCompatibilityEffect(long timestampMicroseconds, double score)
    {
        _effectKind = EffectKind.Compatibility;
        _animator.Reset();
        _ = _animator.Step(timestampMicroseconds, score);
        StartRenderLoop();
        _logger.Information("effect-started", "compatibility");
    }

    private void Execute(EffectTransition transition)
    {
        Interlocked.Exchange(ref _activeGeneration, transition.State.Generation);
        LogRecoveryState(transition.State);
        foreach (EffectCommand command in transition.Commands)
        {
            switch (command.Kind)
            {
                case EffectCommandKind.PrepareOverlay:
                    PrepareOverlay(command.Generation);
                    break;
                case EffectCommandKind.ArmGuard:
                    bool guardArmed = TryGetAvailableGuard(out GuardClient? availableGuard);
                    _armedGuard = guardArmed ? availableGuard : null;
                    Interlocked.Exchange(
                        ref _armedGuardGeneration,
                        guardArmed ? command.Generation : 0);
                    Execute(_safetyMachine.Dispatch(new EffectEvent(
                        EffectEventKind.GuardArmedAcknowledged,
                        _input.CurrentTimestampMicroseconds,
                        command.Generation,
                        guardArmed)));
                    break;
                case EffectCommandKind.RequestHideNative:
                    StartHideOperation(command.Generation);
                    break;
                case EffectCommandKind.BeginGrow:
                    long started = _input.CurrentTimestampMicroseconds;
                    _effectVisibleUntilMicroseconds = started + MinimumAcceptedEffectMicroseconds;
                    Interlocked.Exchange(ref _pendingHideAcknowledgement, 0);
                    Interlocked.Exchange(ref _replacementHeartbeatActive, 1);
                    Interlocked.Exchange(ref _renderHeartbeatGeneration, command.Generation);
                    // This is a bounded hide-to-first-frame hand-off, not an
                    // unlimited render heartbeat. If no frame arrives within
                    // 500 ms the supervisor restores the native cursor.
                    Interlocked.Exchange(ref _lastSuccessfulRenderHeartbeatMicroseconds, started);
                    _logger.Information(
                        "effect-started",
                        $"high-fidelity; quality={_activeCursorQuality}; minimumVisibleMs={MinimumAcceptedEffectMicroseconds / 1_000}");
                    break;
                case EffectCommandKind.BeginRelease:
                    _lastDetectorScore = 0;
                    break;
                case EffectCommandKind.RequestShowNative:
                    StartShowOperation(command.Generation);
                    break;
                case EffectCommandKind.RemoveOverlay:
                    StopVisualEffect();
                    break;
                case EffectCommandKind.RefreshNativeCursor:
                    RefreshNativeCursorAfterOverlayTeardown(command.Generation);
                    break;
                case EffectCommandKind.DisableHighFidelity:
                    _logger.Warning(
                        "high-fidelity-disabled",
                        $"reason={transition.State.LastFaultReason}; disposition={transition.State.LastFaultDisposition}");
                    break;
                case EffectCommandKind.CompleteShutdown:
                    StopVisualEffect();
                    break;
            }
        }
    }

    private void PrepareOverlay(long generation)
    {
        try
        {
            bool ready = _cursorTracker.TryObserve(
                             out CursorObservation cursor,
                             requestedMaximumScale: _settings.MaxScale) &&
                         cursor.IsVisible &&
                         cursor.Image is { SupportsReplacement: true };
            if (ready)
            {
                RememberValidCursor(cursor);
                _activeCursorQuality = cursor.Image!.RenderQuality;
                _overlay.ShowCursor(
                    cursor,
                    1.0,
                    preparedMaximumScale: _settings.MaxScale);
            }

            Execute(_safetyMachine.Dispatch(new EffectEvent(
                EffectEventKind.OverlayReady,
                _input.CurrentTimestampMicroseconds,
                generation,
                ready)));
        }
        catch (Exception exception)
        {
            RecoverFromEngineCallbackFailure("overlay-prepare-failed", exception);
        }
    }

    private void StartHideOperation(long generation)
    {
        if (_shuttingDown || _disposed || _hideInFlight)
        {
            return;
        }

        GuardClient? guard = _armedGuard;
        bool sameArmedGuard = guard is not null &&
                              Interlocked.Read(ref _armedGuardGeneration) == generation &&
                              ReferenceEquals(guard, _guard) &&
                              _guardReady &&
                              guard.IsConnected &&
                              !guard.IsHidden;
        if (!sameArmedGuard)
        {
            Execute(_safetyMachine.Dispatch(new EffectEvent(
                EffectEventKind.HideAcknowledged,
                _input.CurrentTimestampMicroseconds,
                generation,
                Success: false,
                Reason: EffectFaultReason.HideFailed,
                Disposition: FaultDisposition.Recoverable)));
            return;
        }

        _hideInFlight = true;
        _hideTask = HideNativeAsync(guard!, generation, _safetyOperationCancellation.Token);
    }

    private async Task HideNativeAsync(
        GuardClient guard,
        long generation,
        CancellationToken cancellationToken)
    {
        bool success = false;
        try
        {
            success = await guard.HideAsync(generation, GuardLeaseMilliseconds, cancellationToken);
            if (success)
            {
                Interlocked.Exchange(ref _pendingHideGeneration, generation);
                Interlocked.Exchange(
                    ref _pendingHideTimestampMicroseconds,
                    _input.CurrentTimestampMicroseconds);
                Interlocked.Exchange(ref _pendingHideAcknowledgement, 1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("cursor-hide-failed", exception);
        }
        finally
        {
            _hideInFlight = false;
        }

        try
        {
            DispatchSafetyOperationResult(
                new EffectEvent(
                    EffectEventKind.HideAcknowledged,
                    _input.CurrentTimestampMicroseconds,
                    generation,
                    success),
                "hide-ack-dispatch-failed",
                expectedGuard: guard);
        }
        catch (Exception exception)
        {
            _logger.Error("hide-result-processing-failed", exception);
        }
    }

    private void StartShowOperation(long generation)
    {
        if (_shuttingDown || _disposed || _showInFlight)
        {
            return;
        }

        Interlocked.Exchange(ref _replacementHeartbeatActive, 0);
        Interlocked.Exchange(ref _pendingHideAcknowledgement, 0);
        _armedGuard = null;
        Interlocked.Exchange(ref _armedGuardGeneration, 0);
        _showInFlight = true;
        GuardClient guard = _guard;
        _showTask = ShowNativeAsync(guard, generation, _safetyOperationCancellation.Token);
    }

    private async Task ShowNativeAsync(
        GuardClient guard,
        long generation,
        CancellationToken cancellationToken)
    {
        bool success = false;
        try
        {
            success = await guard.ShowAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("cursor-restore-failed", exception);
        }
        finally
        {
            _showInFlight = false;
        }

        try
        {
            DispatchSafetyOperationResult(
                new EffectEvent(
                    EffectEventKind.ShowAcknowledged,
                    _input.CurrentTimestampMicroseconds,
                    generation,
                    success),
                "show-ack-dispatch-failed");
        }
        catch (Exception exception)
        {
            _logger.Error("show-result-processing-failed", exception);
        }
    }

    private void HandleRenderTick(object? sender, EventArgs e)
    {
        try
        {
            HandleRenderTickCore();
        }
        catch (Exception exception)
        {
            RecoverFromEngineCallbackFailure("render-tick-processing-failed", exception);
        }
    }

    private void HandleRenderTickCore()
    {
        if (_shuttingDown || _disposed || _effectKind == EffectKind.None)
        {
            return;
        }

        long now = _input.CurrentTimestampMicroseconds;
        EffectPhase phase = _safetyMachine.State.Phase;
        if (_effectKind == EffectKind.HighFidelity &&
            phase is (EffectPhase.PreparingOverlay
                or EffectPhase.ArmingGuard
                or EffectPhase.AwaitingHideAcknowledgement
                or EffectPhase.NativeHidden
                or EffectPhase.Releasing) &&
            ShouldRecheckRuntimePolicy(now))
        {
            TriggerBlockReason blocked = _policy.Evaluate(
                _settings.DisableWhileDragging,
                _settings.PauseInFullscreen,
                _settings.ExcludedProcesses);
            if (blocked != TriggerBlockReason.None)
            {
                _logger.Information("active-effect-suppressed", blocked.ToString());
                // Runtime policy blocks are transient. Reuse the state
                // machine's non-fatal environmental recovery transition so
                // the native cursor is restored without disabling future
                // high-fidelity effects after the condition clears.
                HandleSafetyEvent(EffectEventKind.DisplayChanging);
                return;
            }
        }

        ShakeActivitySnapshot activity = _detector.AdvanceTimeSnapshot(now);
        _lastDetectorScore = activity.MaximumActiveScore;
        if (now < _previewUntilMicroseconds)
        {
            _lastDetectorScore = 1.0;
        }

        if (_effectKind == EffectKind.Compatibility)
        {
            RenderCompatibility(now);
            return;
        }

        phase = _safetyMachine.State.Phase;
        if (phase == EffectPhase.NativeHidden &&
            ShouldRequestRelease(now, _effectVisibleUntilMicroseconds, _lastDetectorScore))
        {
            Execute(_safetyMachine.Dispatch(new EffectEvent(
                EffectEventKind.ReleaseRequested,
                now,
                _safetyMachine.State.Generation)));
            phase = _safetyMachine.State.Phase;
        }

        double drive = phase switch
        {
            EffectPhase.NativeHidden when now < _effectVisibleUntilMicroseconds =>
                Math.Max(_lastDetectorScore, _acceptedTriggerScore),
            EffectPhase.NativeHidden or EffectPhase.Releasing => _lastDetectorScore,
            _ => 0,
        };
        ScaleAnimationState animation = _animator.Step(now, drive);
        if (!TryGetRenderableCursor(now, out CursorObservation cursor))
        {
            HandleSafetyEvent(EffectEventKind.CursorInvalid);
            return;
        }

        try
        {
            _overlay.ShowCursor(
                cursor,
                animation.CurrentScale,
                preparedMaximumScale: _settings.MaxScale);
        }
        catch (Exception exception)
        {
            RecoverFromEngineCallbackFailure("overlay-render-failed", exception);
            return;
        }

        Execute(_safetyMachine.Dispatch(new EffectEvent(
            EffectEventKind.Tick,
            now,
            _safetyMachine.State.Generation)));

        if (_guard.IsHidden &&
            _safetyMachine.State.Phase is EffectPhase.NativeHidden or EffectPhase.Releasing)
        {
            Interlocked.Exchange(ref _renderHeartbeatGeneration, _safetyMachine.State.Generation);
            Interlocked.Exchange(ref _lastSuccessfulRenderHeartbeatMicroseconds, now);
            Interlocked.Exchange(ref _replacementHeartbeatActive, 1);
        }

        if (_safetyMachine.State.Phase == EffectPhase.Releasing && animation.IsAtRest)
        {
            Execute(_safetyMachine.Dispatch(new EffectEvent(
                EffectEventKind.OverlayAtRest,
                now,
                _safetyMachine.State.Generation)));
        }
    }

    private void RenderCompatibility(long timestampMicroseconds)
    {
        ScaleAnimationState animation = _animator.Step(timestampMicroseconds, _lastDetectorScore);
        if (!NativeCursorPosition.TryGet(out System.Drawing.Point position))
        {
            StopVisualEffect();
            return;
        }

        double progress = Math.Clamp(
            (animation.CurrentScale - 1) / Math.Max(0.1, _settings.MaxScale - 1),
            0,
            1);
        try
        {
            _overlay.ShowLocator(position, progress);
        }
        catch (Exception exception)
        {
            // Compatibility mode never hides the native cursor. A renderer
            // failure can therefore stop the optional locator immediately
            // without risking pointer visibility or the WinForms message loop.
            _logger.Error("compatibility-render-failed", exception);
            StopVisualEffect();
            return;
        }

        if (animation.IsAtRest && timestampMicroseconds >= _previewUntilMicroseconds)
        {
            StopVisualEffect();
        }
    }

    private async Task RunGuardSupervisorAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(GuardSupervisorIntervalMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_shuttingDown || _disposed || !_settings.Enabled ||
                    _settings.Mode != CursorEffectMode.HighFidelity)
                {
                    continue;
                }

                GuardClient guard = _guard;
                long now = _input.CurrentTimestampMicroseconds;
                if (guard.IsHidden)
                {
                    long generation = Interlocked.Read(ref _activeGeneration);
                    bool hideHandoffPending = IsHideHandoffPending(
                        now,
                        generation,
                        Interlocked.Read(ref _pendingHideGeneration),
                        Interlocked.Read(ref _pendingHideTimestampMicroseconds),
                        Volatile.Read(ref _pendingHideAcknowledgement) != 0);
                    if (hideHandoffPending)
                    {
                        // Do not renew during the acknowledgement hand-off.
                        // The Guard's original 1.2 s fail-open lease remains
                        // authoritative if the UI never consumes the callback.
                        continue;
                    }

                    long heartbeatGeneration = Interlocked.Read(ref _renderHeartbeatGeneration);
                    long heartbeat = Interlocked.Read(ref _lastSuccessfulRenderHeartbeatMicroseconds);
                    bool heartbeatFresh = IsRenderHeartbeatFresh(
                        now,
                        generation,
                        heartbeatGeneration,
                        heartbeat,
                        Volatile.Read(ref _replacementHeartbeatActive) != 0);
                    if (!heartbeatFresh || _showInFlight)
                    {
                        if (_showInFlight)
                        {
                            continue;
                        }

                        bool restored = false;
                        try
                        {
                            restored = await guard.ShowAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            _logger.Error("guard-supervisor-stale-render-restore-failed", exception);
                        }

                        _logger.Warning(
                            "guard-supervisor-stale-render",
                            $"heartbeatAgeMs={GetHeartbeatAgeMilliseconds(now, heartbeat)}; restored={restored}");
                        if (ReferenceEquals(guard, _guard))
                        {
                            DispatchSafetyOperationResult(
                                new EffectEvent(
                                    EffectEventKind.HeartbeatLost,
                                    _input.CurrentTimestampMicroseconds,
                                    generation,
                                    restored,
                                    Reason: EffectFaultReason.HeartbeatLost,
                                    Disposition: FaultDisposition.Recoverable),
                                "guard-supervisor-stale-render-dispatch-failed");
                        }

                        continue;
                    }

                    bool renewed = false;
                    try
                    {
                        renewed = await guard.RenewAsync(GuardLeaseMilliseconds, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.Error("guard-supervisor-renew-failed", exception);
                    }

                    if (ReferenceEquals(guard, _guard))
                    {
                        EffectEventKind kind = renewed
                            ? EffectEventKind.LeaseRenewed
                            : EffectEventKind.HeartbeatLost;
                        DispatchSafetyOperationResult(
                            new EffectEvent(
                                kind,
                                _input.CurrentTimestampMicroseconds,
                                generation,
                                renewed,
                                Reason: renewed
                                    ? EffectFaultReason.None
                                    : EffectFaultReason.HeartbeatLost),
                            "guard-supervisor-dispatch-failed");
                    }

                    continue;
                }

                if (guard.IsConnected && _guardReady)
                {
                    continue;
                }

                long nextProbe = Interlocked.Read(ref _nextGuardProbeMicroseconds);
                if (nextProbe > 0 && now >= 0 && now < nextProbe)
                {
                    continue;
                }

                bool ready = false;
                try
                {
                    ready = await ReprobeGuardAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Error("guard-supervisor-probe-failed", exception);
                }

                if (ready)
                {
                    _guardProbeFailures = 0;
                    Interlocked.Exchange(ref _nextGuardProbeMicroseconds, 0);
                    ExecuteOnUi(() =>
                    {
                        ResetHighFidelityFaultIfSafe();
                        _logger.Information("guard-supervisor-healthy");
                    });
                }
                else
                {
                    int failure = Math.Min(32, ++_guardProbeFailures);
                    long delay = Math.Min(
                        5_000_000,
                        GuardSupervisorIntervalMilliseconds * 1_000L *
                        (1L << Math.Min(failure - 1, 4)));
                    Interlocked.Exchange(
                        ref _nextGuardProbeMicroseconds,
                        now > long.MaxValue - delay ? long.MaxValue : now + delay);
                    _logger.Warning(
                        "guard-supervisor-backoff",
                        $"attempt={failure}; retryMs={delay / 1_000}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HandleDeviceTopologyChanged(object? sender, EventArgs e)
    {
        try
        {
            _detector.ResetAll();
            _normalizer.Reset();
            _policy.ResetPointerState();
            if (_effectKind == EffectKind.None &&
                !_guard.IsHidden &&
                _safetyMachine.State.NativeVisibility == NativeCursorVisibility.KnownShown)
            {
                _logger.Information("input-device-removed", "idle-state-reset");
                return;
            }

            // Removal during an effect can invalidate button state and the
            // meaning of in-flight raw deltas. Restore the native cursor before
            // clearing the visual state.
            HandleSafetyEvent(EffectEventKind.DeviceLost);
        }
        catch (Exception exception)
        {
            RecoverFromEngineCallbackFailure("device-topology-processing-failed", exception);
        }
    }

    private void HandleOverlayDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        try
        {
            InvalidateCursorForDisplayChange();
        }
        catch (Exception exception)
        {
            RecoverFromEngineCallbackFailure("overlay-dpi-processing-failed", exception);
        }
    }

    private void HandleGuardLost(object? sender, EventArgs e) => ExecuteOnUi(() =>
    {
        if (!ReferenceEquals(sender, _guard))
        {
            return;
        }

        _guardReady = false;
        long now = _input.CurrentTimestampMicroseconds;
        Interlocked.Exchange(
            ref _nextGuardProbeMicroseconds,
            now > long.MaxValue - 250_000 ? long.MaxValue : now + 250_000);
        HandleSafetyEvent(EffectEventKind.GuardExited);
    });

    private void ExecuteOnUi(Action action)
    {
        if (_shuttingDown || _disposed)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            ExecuteUiActionSafely(action);
            return;
        }

        try
        {
            _uiContext.Post(static state =>
            {
                UiWorkItem workItem = (UiWorkItem)state!;
                if (!workItem.Owner._shuttingDown && !workItem.Owner._disposed)
                {
                    workItem.Owner.ExecuteUiActionSafely(workItem.Action);
                }
            }, new UiWorkItem(this, action));
        }
        catch (Exception exception)
        {
            _logger.Error("ui-safety-dispatch-failed", exception);
        }
    }

    private void ExecuteUiActionSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            RecoverFromEngineCallbackFailure("ui-safety-callback-failed", exception);
        }
    }

    private void RecoverFromEngineCallbackFailure(string eventName, Exception exception)
    {
        try
        {
            _logger.Error(eventName, exception);
        }
        catch (Exception)
        {
            // Diagnostics are secondary to restoring the native cursor.
        }

        if (_shuttingDown || _disposed)
        {
            return;
        }

        bool nativeMayBeHidden = _guard.IsHidden ||
                                 _safetyMachine.State.NativeVisibility != NativeCursorVisibility.KnownShown;
        if (!nativeMayBeHidden &&
            (_effectKind == EffectKind.Compatibility || _settings.Mode == CursorEffectMode.Compatibility))
        {
            TryEngineCleanup(StopVisualEffect, "compatibility-stop-after-failure-failed");
            return;
        }

        try
        {
            // OverlayFailed is fatal for this high-fidelity session. The state
            // machine requests Show before removing the replacement overlay.
            HandleSafetyEvent(EffectEventKind.OverlayFailed);
        }
        catch (Exception recoveryException)
        {
            try
            {
                _logger.Error("callback-recovery-failed", recoveryException);
            }
            catch (Exception)
            {
            }

            if (!nativeMayBeHidden && !_guard.IsHidden)
            {
                TryEngineCleanup(_overlay.HideOverlay, "overlay-emergency-hide-failed");
            }

            if ((nativeMayBeHidden || _guard.IsHidden) && !_showInFlight)
            {
                try
                {
                    StartShowOperation(_safetyMachine.State.Generation);
                }
                catch (Exception showException)
                {
                    try
                    {
                        _logger.Error("emergency-cursor-restore-start-failed", showException);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }

    private bool ShouldRecheckRuntimePolicy(long timestampMicroseconds)
    {
        long previous = _lastRuntimePolicyCheckMicroseconds;
        if (previous > 0 && timestampMicroseconds >= previous &&
            timestampMicroseconds - previous < RuntimePolicyRecheckIntervalMicroseconds)
        {
            return false;
        }

        _lastRuntimePolicyCheckMicroseconds = timestampMicroseconds;
        return true;
    }

    private bool TryGetAvailableGuard(out GuardClient? guard)
    {
        GuardClient snapshot = _guard;
        bool available = _guardReady &&
                         snapshot.IsConnected &&
                         !snapshot.IsHidden;
        guard = available ? snapshot : null;
        return available;
    }

    private bool TryGetRenderableCursor(long timestampMicroseconds, out CursorObservation cursor)
    {
        bool observed = _cursorTracker.TryObserve(
            out cursor,
            allowCachedWhenSystemHidden: _guard.IsHidden,
            requestedMaximumScale: _settings.MaxScale);
        if (observed && cursor.Image is { SupportsReplacement: true })
        {
            RememberValidCursor(cursor);
            return true;
        }

        if (_cursorObservationFailureFrames == 0)
        {
            _firstCursorObservationFailureMicroseconds = timestampMicroseconds;
            _logger.Warning("cursor-observation-transient");
        }

        _cursorObservationFailureFrames++;
        bool withinTimeGrace = timestampMicroseconds < _firstCursorObservationFailureMicroseconds ||
                               timestampMicroseconds - _firstCursorObservationFailureMicroseconds <=
                               CursorObservationGraceMicroseconds;
        if (_hasLastValidCursor &&
            _cursorObservationFailureFrames <= CursorObservationGraceFrames &&
            withinTimeGrace &&
            _lastValidCursor.Image is { SupportsReplacement: true })
        {
            Point position = _lastValidCursor.Position;
            if (NativeCursorPosition.TryGet(out Point currentPosition))
            {
                position = currentPosition;
            }

            cursor = _lastValidCursor with { Position = position, IsVisible = true };
            return true;
        }

        cursor = default;
        return false;
    }

    private void RememberValidCursor(CursorObservation cursor)
    {
        _lastValidCursor = cursor;
        _hasLastValidCursor = cursor.Image is { SupportsReplacement: true };
        _cursorObservationFailureFrames = 0;
        _firstCursorObservationFailureMicroseconds = 0;
    }

    private static long GetHeartbeatAgeMilliseconds(long now, long heartbeat)
    {
        if (heartbeat < 0)
        {
            return -1;
        }

        if (now < heartbeat)
        {
            return 0;
        }

        return (now - heartbeat) / 1_000;
    }

    internal static bool ShouldRequestRelease(
        long timestampMicroseconds,
        long visibleUntilMicroseconds,
        double detectorScore) =>
        detectorScore <= 0 &&
        timestampMicroseconds >= visibleUntilMicroseconds;

    internal static bool IsRenderHeartbeatFresh(
        long timestampMicroseconds,
        long generation,
        long heartbeatGeneration,
        long heartbeatTimestampMicroseconds,
        bool replacementActive) =>
        replacementActive &&
        generation == heartbeatGeneration &&
        heartbeatTimestampMicroseconds >= 0 &&
        (timestampMicroseconds < heartbeatTimestampMicroseconds ||
         timestampMicroseconds - heartbeatTimestampMicroseconds <=
         MaximumRenderHeartbeatAgeMicroseconds);

    internal static bool IsHideHandoffPending(
        long timestampMicroseconds,
        long generation,
        long pendingGeneration,
        long pendingTimestampMicroseconds,
        bool acknowledgementPending) =>
        acknowledgementPending &&
        generation == pendingGeneration &&
        pendingTimestampMicroseconds >= 0 &&
        (timestampMicroseconds < pendingTimestampMicroseconds ||
         timestampMicroseconds - pendingTimestampMicroseconds <=
         MaximumRenderHeartbeatAgeMicroseconds);

    private void LogRecoveryState(EffectState state)
    {
        if (state.LastFaultReason == EffectFaultReason.None ||
            state.LastFaultTimestampMicroseconds == _lastLoggedFaultTimestampMicroseconds)
        {
            return;
        }

        _lastLoggedFaultTimestampMicroseconds = state.LastFaultTimestampMicroseconds;
        long remaining = state.RecoveryDeadlineMicroseconds <= state.LastFaultTimestampMicroseconds
            ? 0
            : state.RecoveryDeadlineMicroseconds - state.LastFaultTimestampMicroseconds;
        _logger.Warning(
            "high-fidelity-fault",
            $"reason={state.LastFaultReason}; disposition={state.LastFaultDisposition}; " +
            $"recovery={state.RecoveryStatus}; cooldownMs={remaining / 1_000}; " +
            $"consecutive={state.ConsecutiveRecoverableFaults}");
    }

    private void StartRenderLoop()
    {
        if (_renderTimer.Enabled)
        {
            return;
        }

        _timerResolution = new HighResolutionTimerLease();
        _renderTimer.Start();
    }

    private void StopVisualEffect()
    {
        // Cleanup remains best-effort and ordered. One failed native/UI call
        // must not prevent later owners from being released or leave the
        // render timer spinning after the native cursor has been restored.
        TryEngineCleanup(_overlay.HideOverlay, "overlay-hide-failed");
        TryEngineCleanup(_renderTimer.Stop, "render-timer-stop-failed");
        TryEngineCleanup(() => _timerResolution?.Dispose(), "timer-resolution-release-failed");
        _timerResolution = null;
        _effectKind = EffectKind.None;
        _previewUntilMicroseconds = 0;
        _lastDetectorScore = 0;
        _lastRuntimePolicyCheckMicroseconds = 0;
        _effectVisibleUntilMicroseconds = 0;
        _acceptedTriggerScore = 0;
        _cursorObservationFailureFrames = 0;
        _firstCursorObservationFailureMicroseconds = 0;
        Interlocked.Exchange(ref _replacementHeartbeatActive, 0);
        Interlocked.Exchange(ref _lastSuccessfulRenderHeartbeatMicroseconds, -1);
        _armedGuard = null;
        Interlocked.Exchange(ref _armedGuardGeneration, 0);
        _animator.Reset();
        _logger.Information("effect-stopped");
        TryEngineCleanup(ApplyPendingCursorInvalidationIfSafe, "cursor-invalidation-after-stop-failed");
    }

    private void RefreshNativeCursorAfterOverlayTeardown(long generation)
    {
        if (!CanRefreshNativeCursor(generation))
        {
            return;
        }

        TryRefreshNativeCursor("cursor-refresh-after-effect-failed");
        _cursorRecoveryTimer.Stop();
        _cursorRecoveryGeneration = generation;
        _cursorRecoveryAttempt = 0;
        _cursorRecoveryPosition = System.Windows.Forms.Cursor.Position;
        _cursorRecoveryTimer.Interval = 16;
        _cursorRecoveryTimer.Start();
        try
        {
            // Run once more after the UI loop observes the overlay's SW_HIDE.
            // The generation check prevents this deferred refresh from leaking
            // into a newer preview or gesture.
            _uiContext.Post(
                static state =>
                {
                    CursorRefreshWorkItem workItem = (CursorRefreshWorkItem)state!;
                    if (workItem.Owner.CanRefreshNativeCursor(workItem.Generation))
                    {
                        workItem.Owner.TryRefreshNativeCursor(
                            "cursor-deferred-refresh-after-effect-failed");
                    }
                },
                new CursorRefreshWorkItem(this, generation));
        }
        catch (Exception exception)
        {
            _logger.Error("cursor-refresh-dispatch-failed", exception);
        }
    }

    private bool CanRefreshNativeCursor(long generation) =>
        !_shuttingDown &&
        !_disposed &&
        _effectKind == EffectKind.None &&
        !_overlay.IsVisible &&
        !_guard.IsHidden &&
        _safetyMachine.State.NativeVisibility == NativeCursorVisibility.KnownShown &&
        _safetyMachine.State.Generation == generation;

    private void HandleCursorRecoveryTick(object? sender, EventArgs e)
    {
        _cursorRecoveryTimer.Stop();
        if (!CanRefreshNativeCursor(_cursorRecoveryGeneration) ||
            System.Windows.Forms.Cursor.Position != _cursorRecoveryPosition)
        {
            return;
        }

        TryRefreshNativeCursor("cursor-stationary-recovery-unconfirmed");
        _cursorRecoveryAttempt++;
        if (_cursorRecoveryAttempt < 4)
        {
            _cursorRecoveryTimer.Interval = 16 << _cursorRecoveryAttempt;
            _cursorRecoveryTimer.Start();
        }
    }

    private void TryRefreshNativeCursor(string failureEventName)
    {
        try
        {
            if (!_cursorRefresher.TryRefreshAtCurrentPosition())
            {
                _logger.Warning(failureEventName, $"Native cursor redraw was not confirmed; generation={_safetyMachine.State.Generation}; overlay={_overlay.IsVisible}; guardHidden={_guard.IsHidden}.");
            }
        }
        catch (Exception exception)
        {
            // Cursor redraw is best effort after MagShowSystemCursor(TRUE).
            // Never reinterpret a successful visibility acknowledgement as a
            // hide/show ownership failure.
            _logger.Error(failureEventName, exception);
        }
    }

    private void DispatchSafetyOperationResult(
        EffectEvent effectEvent,
        string failureEventName,
        GuardClient? expectedGuard = null)
    {
        if (_shuttingDown || _disposed)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            ExecuteOnUi(() => DispatchSafetyOperationResult(effectEvent, failureEventName, expectedGuard));
            return;
        }

        try
        {
            if (effectEvent.Kind == EffectEventKind.HideAcknowledged)
            {
                Interlocked.Exchange(ref _pendingHideAcknowledgement, 0);
                bool acknowledgementStillValid = expectedGuard is not null &&
                                                 ReferenceEquals(expectedGuard, _guard) &&
                                                 expectedGuard.IsConnected &&
                                                 expectedGuard.IsHidden;
                if (effectEvent.Success && !acknowledgementStillValid)
                {
                    _logger.Warning("stale-hide-acknowledgement-rejected");
                    effectEvent = effectEvent with
                    {
                        Success = false,
                        Reason = EffectFaultReason.HideFailed,
                        Disposition = FaultDisposition.Recoverable,
                    };
                }
            }

            Execute(_safetyMachine.Dispatch(effectEvent));
        }
        catch (Exception exception)
        {
            _logger.Error(failureEventName, exception);
            if (_guard.IsHidden)
            {
                StartShowOperation(effectEvent.Generation);
            }
        }
    }

    private async Task AwaitSafetyOperationsAsync()
    {
        Task[] pending = new[] { _hideTask, _showTask, _guardSupervisorTask }
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch (Exception exception)
        {
            // Every operation owns its exception handling. Keep this final
            // barrier defensive so shutdown can still restore the cursor.
            _logger.Error("safety-operation-drain-failed", exception);
        }
    }

    private async Task<bool> RestoreNativeCursorForShutdownAsync()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (await _guard.ShowAsync(CancellationToken.None))
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                _logger.Error("cursor-shutdown-restore-failed", exception);
            }

            if (attempt < 3)
            {
                await Task.Delay(100);
            }
        }

        return !_guard.IsHidden;
    }

    private async Task<bool> ReprobeGuardAsync(CancellationToken cancellationToken)
    {
        await _guardLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_shuttingDown || _disposed)
            {
                return false;
            }

            if (_guard.IsConnected)
            {
                _guardReady = true;
                return true;
            }

            string? guardPath = FindGuardExecutable();
            if (guardPath is null)
            {
                _guardReady = false;
                _logger.Warning("guard-reprobe-not-found");
                return false;
            }

            GuardClient candidate = new();
            candidate.GuardLost += HandleGuardLost;
            bool ready;
            try
            {
                ready = await candidate.StartAsync(guardPath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                candidate.GuardLost -= HandleGuardLost;
                await DisposeGuardQuietlyAsync(candidate);
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error("guard-reprobe-failed", exception);
                candidate.GuardLost -= HandleGuardLost;
                await DisposeGuardQuietlyAsync(candidate);
                _guardReady = false;
                return false;
            }

            if (!ready || !candidate.IsConnected)
            {
                candidate.GuardLost -= HandleGuardLost;
                await DisposeGuardQuietlyAsync(candidate);
                _guardReady = false;
                return false;
            }

            GuardClient previous = _guard;
            if (previous.IsHidden ||
                Volatile.Read(ref _pendingHideAcknowledgement) != 0 ||
                Volatile.Read(ref _replacementHeartbeatActive) != 0)
            {
                candidate.GuardLost -= HandleGuardLost;
                await DisposeGuardQuietlyAsync(candidate);
                _guardReady = false;
                _logger.Warning("guard-reprobe-deferred", "previous-guard-still-owns-cursor-safety");
                return false;
            }

            previous.GuardLost -= HandleGuardLost;
            _guard = candidate;
            _guardReady = true;
            // The candidate is published atomically while the previous Guard
            // is proven not to own cursor visibility. Retiring the old process
            // must never hold the lifecycle gate or stop the new supervisor.
            _ = RetireGuardAsync(previous);
            _logger.Information("guard-reprobe-ready");
            return true;
        }
        finally
        {
            _guardLifecycleGate.Release();
        }
    }

    private void ResetHighFidelityFaultIfSafe()
    {
        if (!_settings.Enabled ||
            _settings.Mode != CursorEffectMode.HighFidelity ||
            !_guardReady ||
            !_guard.IsConnected ||
            _guard.IsHidden ||
            _safetyMachine.State.NativeVisibility != NativeCursorVisibility.KnownShown)
        {
            return;
        }

        Execute(_safetyMachine.Dispatch(new EffectEvent(
            EffectEventKind.ResetFault,
            _input.CurrentTimestampMicroseconds,
            _safetyMachine.State.Generation)));
        if (!_safetyMachine.State.HighFidelityDisabled &&
            _safetyMachine.State.RecoveryStatus == EffectRecoveryStatus.Healthy)
        {
            _logger.Information("high-fidelity-recovered");
        }
    }

    private async Task DisposeGuardQuietlyAsync(GuardClient guard)
    {
        try
        {
            await guard.DisposeAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("guard-dispose-failed", exception);
        }
    }

    private async Task RetireGuardAsync(GuardClient guard)
    {
        Task disposal = DisposeGuardQuietlyAsync(guard);
        Task completed = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(3)))
            .ConfigureAwait(false);
        if (completed != disposal)
        {
            _logger.Warning("guard-retirement-deferred", "old-guard-retains-independent-owner-exit-recovery");
            return;
        }

        await disposal.ConfigureAwait(false);
    }

    private async Task AwaitTaskSafelyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (Exception exception)
        {
            _logger.Error("safety-operation-wait-failed", exception);
        }
    }

    private void ApplyPendingCursorInvalidationIfSafe()
    {
        if (!_cursorInvalidationPending || _guard.IsHidden || _showInFlight ||
            _safetyMachine.State.NativeVisibility != NativeCursorVisibility.KnownShown)
        {
            return;
        }

        _cursorTracker.Invalidate();
        _cursorInvalidationPending = false;
    }

    private void TryEngineCleanup(Action cleanup, string eventName)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            _logger.Error(eventName, exception);
        }
    }

    private ScaleAnimator CreateAnimator() => new(new ScaleAnimationOptions
    {
        MaximumScale = _settings.MaxScale,
        ActiveScoreThreshold = 0.30,
        AttackTimeConstantMicroseconds = 75_000,
        HoldMicroseconds = 90_000,
        ReleaseTimeConstantMicroseconds = 280_000,
        AttackDampingRatio = 1.0,
        ReleaseDampingRatio = 0.78,
        MinimumScale = 0.975,
    });

    private static EffectStateMachine CreateSafetyMachine() => new(new EffectStateMachineOptions
    {
        // The native Guard renews on a background supervisor every 250 ms with
        // a 1.2 s lease. The reducer deadline is intentionally wider so a busy
        // UI thread cannot falsely declare an independently renewed lease dead.
        HiddenLeaseMicroseconds = 2_500_000,
        ShowRetryMicroseconds = 100_000,
        RecoveryCooldownMicroseconds = 250_000,
        MaximumRecoveryCooldownMicroseconds = 5_000_000,
        RecoveryBackoffFactor = 2,
    });

    internal static string? FindGuardExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("FOXMOUSE_GUARD_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && IsUsableGuardExecutable(configured))
        {
            return Path.GetFullPath(configured);
        }

        string besideApp = Path.Combine(AppContext.BaseDirectory, "FoxMouse.Guard.exe");
        if (IsUsableGuardExecutable(besideApp))
        {
            return besideApp;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !string.Equals(directory.Name, "FoxMouse", StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        string guardBuildRoot = Path.Combine(directory.FullName, "src", "FoxMouse.Guard", "bin");
        if (!Directory.Exists(guardBuildRoot))
        {
            return null;
        }

        return Directory.EnumerateFiles(guardBuildRoot, "FoxMouse.Guard.exe", SearchOption.AllDirectories)
            .Where(IsUsableGuardExecutable)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsUsableGuardExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // A framework-dependent apphost requires its managed entry assembly.
        // A single-file deployment has no adjacent deps.json and remains valid.
        string depsPath = Path.ChangeExtension(path, ".deps.json");
        string assemblyPath = Path.ChangeExtension(path, ".dll");
        return !File.Exists(depsPath) || File.Exists(assemblyPath);
    }

    private enum EffectKind
    {
        None,
        HighFidelity,
        Compatibility,
    }

    private sealed record UiWorkItem(FoxMouseEngine Owner, Action Action);

    private sealed record CursorRefreshWorkItem(FoxMouseEngine Owner, long Generation);
}
