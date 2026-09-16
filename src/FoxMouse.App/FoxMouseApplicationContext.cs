using FoxMouse.App.UI;
using FoxMouse.Core;
using FoxMouse.Platform.Windows.Configuration;
using FoxMouse.Platform.Windows.Diagnostics;
using Microsoft.Win32;

namespace FoxMouse.App;

internal sealed class FoxMouseApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore;
    private readonly RollingFileLogger _logger;
    private readonly FoxMouseEngine _engine;
    private readonly SettingsHostBridge _settingsHostBridge;
    private readonly WindowsThemeService _themeService;
    private Icon _applicationIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ModernTrayMenuForm _trayMenu;
    private readonly System.Windows.Forms.Timer _startupTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly SynchronizationContext _uiContext;
    private readonly int _uiThreadId;
    private readonly bool _lifecycleSmokeMode;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private FoxMouseSettings _settings;
    private Task _initializationTask = Task.CompletedTask;
    private Task _userOperationTask = Task.CompletedTask;
    private Task _shutdownTask = Task.CompletedTask;
    private Form? _activeDialog;
    private bool _userOperationInProgress;
    private bool _initializationSucceeded;
    private bool _lifecycleSmokeGuardReady;
    private bool _lifecycleSmokeDialogClosed;
    private bool _lifecycleSmokeDialogCloseRequested;
    private bool _externalSettingsReloadPending;
    private bool _externalPreviewPending;
    private int _shutdownState;

    private FoxMouseApplicationContext(
        SettingsStore settingsStore,
        RollingFileLogger logger,
        FoxMouseEngine engine,
        FoxMouseSettings settings,
        bool lifecycleSmokeMode = false)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        _engine = engine;
        _settings = settings;
        UiText.Configure(settings.Language);
        _lifecycleSmokeMode = lifecycleSmokeMode;
        _themeService = new WindowsThemeService();
        _applicationIcon = FoxMouseIconFactory.CreateApplicationIcon(_themeService.Colors);
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _uiThreadId = Environment.CurrentManagedThreadId;
        _settingsHostBridge = new SettingsHostBridge(
            _settingsStore,
            _logger,
            _uiContext,
            QueueExternalSettingsReload,
            QueueExternalPreview,
            listenForChanges: !_lifecycleSmokeMode);

        _trayMenu = new ModernTrayMenuForm(_themeService);
        _trayMenu.EnabledToggleRequested += HandleEnabledClicked;
        _trayMenu.PreviewRequested += HandlePreviewClicked;
        _trayMenu.SettingsRequested += HandleSettingsClicked;
        _trayMenu.DiagnosticsRequested += HandleOpenLogsClicked;
        _trayMenu.AboutRequested += HandleAboutClicked;
        _trayMenu.ExitRequested += HandleExitClicked;

        _notifyIcon = new NotifyIcon
        {
            Text = FoxMouse.Core.UiText.Get("TrayHint"),
            Icon = _applicationIcon,
            Visible = true,
        };
        _notifyIcon.MouseUp += HandleNotifyIconMouseUp;
        _notifyIcon.DoubleClick += HandleSettingsClicked;

        _startupTimer = new System.Windows.Forms.Timer { Interval = 1 };
        _startupTimer.Tick += HandleStartupTick;
        _startupTimer.Start();
        _statusTimer = new System.Windows.Forms.Timer { Interval = 1_000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        SystemEvents.SessionSwitch += HandleSessionSwitch;
        SystemEvents.PowerModeChanged += HandlePowerModeChanged;
        SystemEvents.DisplaySettingsChanging += HandleDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged += HandleDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += HandleUserPreferenceChanged;
        RefreshStatus();
    }

    public static FoxMouseApplicationContext Create()
    {
        SettingsStore settingsStore = new();
        // No WinForms handle exists yet, so this bounded local read can safely
        // complete before UI construction. Guard I/O starts after the message
        // loop is running; blocking on it here would deadlock the UI context.
        FoxMouseSettings settings = settingsStore.LoadAsync()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        string logsDirectory = Path.Combine(settingsStore.RootDirectory, "Logs");
        RollingFileLogger logger = new(logsDirectory);
        FoxMouseEngine engine = new(settings, logger);
        return new FoxMouseApplicationContext(settingsStore, logger, engine, settings);
    }

    internal static FoxMouseApplicationContext CreateForLifecycleSmoke(string temporaryRoot)
    {
        SettingsStore settingsStore = new(temporaryRoot);
        FoxMouseSettings settings = SettingsNormalizer.Normalize(FoxMouseSettings.Default with
        {
            Enabled = false,
            StartWithWindows = false,
        });
        RollingFileLogger logger = new(Path.Combine(temporaryRoot, "Logs"));
        FoxMouseEngine engine = new(settings, logger);
        return new FoxMouseApplicationContext(
            settingsStore,
            logger,
            engine,
            settings,
            lifecycleSmokeMode: true);
    }

    internal bool LifecycleSmokeInitializationSucceeded => _initializationSucceeded;

    internal bool LifecycleSmokeGuardReady => _lifecycleSmokeGuardReady;

    internal bool LifecycleSmokeDialogClosed => _lifecycleSmokeDialogClosed;

    internal bool ShutdownCompleted => Volatile.Read(ref _shutdownState) == 2;

    protected override void ExitThreadCore()
    {
        if (Volatile.Read(ref _shutdownState) == 2)
        {
            base.ExitThreadCore();
            return;
        }

        if (Interlocked.CompareExchange(ref _shutdownState, 1, 0) != 0)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            BeginShutdown();
        }
        else
        {
            try
            {
                _uiContext.Post(
                    static state => ((FoxMouseApplicationContext)state!).BeginShutdown(),
                    this);
            }
            catch (Exception exception)
            {
                _logger.Error("shutdown-dispatch-failed", exception);
            }
        }
    }

    private void BeginShutdown()
    {
        // Retain the task even though ApplicationContext has no async exit
        // hook. ShutdownAsync contains its own exception boundary and keeps
        // the WinForms loop alive until cursor recovery has completed.
        _shutdownTask = ShutdownAsync();
    }

    private void HandleStartupTick(object? sender, EventArgs e)
    {
        _startupTimer.Stop();
        _startupTimer.Tick -= HandleStartupTick;
        _initializationTask = InitializeApplicationAsync();
    }

    private async Task InitializeApplicationAsync()
    {
        bool succeeded = false;
        try
        {
            await _engine.InitializeAsync(_lifetimeCancellation.Token);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            if (!_lifecycleSmokeMode)
            {
                StartupRegistration.SetEnabled(_settings.StartWithWindows, ExecutableLocator.Current);
            }

            _logger.Information(
                "application-started",
                $"version={Application.ProductVersion}; os={Environment.OSVersion.Version}");
            succeeded = true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            _logger.Error("application-initialization-failed", exception);
        }
        catch (Exception exception)
        {
            // Startup failures degrade to compatibility mode; they must never
            // become an unobserved exception on the WinForms message loop.
            _logger.Error("application-initialization-failed", exception);
        }
        finally
        {
            _initializationSucceeded = succeeded;
            _lifecycleSmokeGuardReady = _engine.IsGuardReady;
            if (!IsShuttingDown)
            {
                RefreshStatus();
                if (_lifecycleSmokeMode)
                {
                    // Exercise a real modal settings loop before shutdown. The
                    // next timer tick must close it without user interaction.
                    _startupTimer.Interval = 150;
                    _startupTimer.Tick += HandleLifecycleSmokeDialogTick;
                    _startupTimer.Start();
                }
            }
        }
    }

    private void HandleLifecycleSmokeDialogTick(object? sender, EventArgs e)
    {
        _startupTimer.Stop();
        _startupTimer.Tick -= HandleLifecycleSmokeDialogTick;
        _userOperationTask = RunUserOperationAsync(ShowSettingsAsync);
        _startupTimer.Interval = 150;
        _startupTimer.Tick += HandleLifecycleSmokeExitTick;
        _startupTimer.Start();
    }

    private void HandleLifecycleSmokeExitTick(object? sender, EventArgs e)
    {
        _startupTimer.Stop();
        _startupTimer.Tick -= HandleLifecycleSmokeExitTick;
        ExitThread();
    }

    private async Task ShutdownAsync()
    {
        TryCleanup(() => SystemEvents.SessionSwitch -= HandleSessionSwitch);
        TryCleanup(() => SystemEvents.PowerModeChanged -= HandlePowerModeChanged);
        TryCleanup(() => SystemEvents.DisplaySettingsChanging -= HandleDisplaySettingsChanging);
        TryCleanup(() => SystemEvents.DisplaySettingsChanged -= HandleDisplaySettingsChanged);
        TryCleanup(() => SystemEvents.UserPreferenceChanged -= HandleUserPreferenceChanged);
        TryCleanup(_startupTimer.Stop);
        TryCleanup(_statusTimer.Stop);
        TryCleanup(_trayMenu.Hide);
        TryCleanup(() => _notifyIcon.Text = FoxMouse.Core.UiText.Get("Exiting"));
        TryCleanup(_lifetimeCancellation.Cancel);
        TryCleanup(CloseActiveDialogForShutdown);

        try
        {
            await AwaitShutdownStepAsync(_initializationTask, "initialization-drain-failed");
            await AwaitShutdownStepAsync(_userOperationTask, "user-operation-drain-failed");

            try
            {
                await _settingsHostBridge.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.Error("settings-host-bridge-shutdown-failed", exception);
            }

            // Engine disposal owns native-cursor recovery and must execute even
            // when either earlier task has faulted unexpectedly.
            try
            {
                await _engine.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.Error("engine-shutdown-failed", exception);
            }
            // Settings must still close if engine teardown reports a failure.
            try
            {
                if (!_lifecycleSmokeMode)
                {
                    await SettingsShutdownChannel.RequestAsync(_settingsStore.SettingsPath);
                }
            }
            catch (Exception exception)
            {
                _logger.Error("settings-window-shutdown-failed", exception);
            }
            _logger.Information("application-stopped");
        }
        finally
        {
            try
            {
                await _logger.DisposeAsync();
            }
            catch (Exception)
            {
                // Cursor restoration has already run. A logging failure must
                // not prevent the message loop from terminating.
            }

            TryCleanup(() => _notifyIcon.Visible = false);
            TryCleanup(() => _notifyIcon.MouseUp -= HandleNotifyIconMouseUp);
            TryCleanup(() => _notifyIcon.DoubleClick -= HandleSettingsClicked);
            TryCleanup(_notifyIcon.Dispose);
            TryCleanup(_trayMenu.Dispose);
            TryCleanup(_applicationIcon.Dispose);
            TryCleanup(_startupTimer.Dispose);
            TryCleanup(_statusTimer.Dispose);
            TryCleanup(_lifetimeCancellation.Dispose);
            Volatile.Write(ref _shutdownState, 2);
            base.ExitThreadCore();
        }
    }

    private void HandleEnabledClicked(object? sender, EventArgs e)
    {
        if (IsShuttingDown || _userOperationInProgress)
        {
            RefreshStatus();
            return;
        }

        bool requestedState = !_settings.Enabled;
        _userOperationTask = RunUserOperationAsync(() => ApplyEnabledSettingAsync(requestedState));
    }

    private async Task ApplyEnabledSettingAsync(bool enabled)
    {
        _settings = _settings with { Enabled = enabled };
        await _engine.ApplySettingsAsync(_settings, _lifetimeCancellation.Token);
        await SaveSettingsAsync();
        RefreshStatus();
    }

    private void HandleSettingsClicked(object? sender, EventArgs e)
    {
        if (IsShuttingDown || _userOperationInProgress)
        {
            return;
        }

        _userOperationTask = RunUserOperationAsync(ShowSettingsAsync);
    }

    private async Task ShowSettingsAsync()
    {
        if (!_lifecycleSmokeMode &&
            await _settingsHostBridge.TryOpenAsync("general", _lifetimeCancellation.Token))
        {
            return;
        }

        if (IsShuttingDown)
        {
            return;
        }

        using SettingsForm form = new(_settings);
        if (ShowTrackedDialog(form) != DialogResult.OK || IsShuttingDown || _lifecycleSmokeMode)
        {
            return;
        }

        _settings = form.Result;
        await _engine.ApplySettingsAsync(_settings, _lifetimeCancellation.Token);
        try
        {
            StartupRegistration.SetEnabled(
                _settings.StartWithWindows,
                ExecutableLocator.Current);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            _logger.Error("startup-registration-failed", exception);
            MessageBox.Show(
                FoxMouse.Core.UiText.Get("StartupSaveError"),
                "FoxMouse",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        await SaveSettingsAsync();
        RefreshStatus();
    }

    private async Task RunUserOperationAsync(Func<Task> operation)
    {
        _userOperationInProgress = true;
        try
        {
            // Yield once so the caller can retain this Task before a modal
            // ShowDialog starts its nested message loop.
            await Task.Yield();
            if (IsShuttingDown)
            {
                return;
            }

            await operation();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("user-operation-failed", exception);
            if (!IsShuttingDown)
            {
                MessageBox.Show(
                    FoxMouse.Core.UiText.Get("OperationSafeError"),
                    "FoxMouse",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _userOperationInProgress = false;
            TryStartExternalSettingsOperation();
        }
    }

    private void HandleOpenLogsClicked(object? sender, EventArgs e)
    {
        Directory.CreateDirectory(_settingsStore.RootDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _settingsStore.RootDirectory,
            UseShellExecute = true,
        });
    }

    private void HandleNotifyIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || IsShuttingDown)
        {
            return;
        }

        RefreshStatus();
        _trayMenu.ToggleAt(Cursor.Position);
    }

    private void HandlePreviewClicked(object? sender, EventArgs e)
    {
        if (!IsShuttingDown)
        {
            _engine.Preview();
        }
    }

    private void HandleExitClicked(object? sender, EventArgs e) => ExitThread();

    private void HandleAboutClicked(object? sender, EventArgs e)
    {
        if (IsShuttingDown || _userOperationInProgress)
        {
            return;
        }

        _userOperationTask = RunUserOperationAsync(ShowAboutAsync);
    }

    private async Task ShowAboutAsync()
    {
        if (!_lifecycleSmokeMode &&
            await _settingsHostBridge.TryOpenAsync("about", _lifetimeCancellation.Token))
        {
            return;
        }

        if (IsShuttingDown)
        {
            return;
        }

        using AboutForm form = new(_themeService, _applicationIcon);
        _ = ShowTrackedDialog(form);
    }

    private DialogResult ShowTrackedDialog(Form form)
    {
        if (IsShuttingDown)
        {
            return DialogResult.Cancel;
        }

        form.Icon = _applicationIcon;
        _themeService.ApplyTo(form);
        _activeDialog = form;
        try
        {
            return form.ShowDialog();
        }
        finally
        {
            if (ReferenceEquals(_activeDialog, form))
            {
                _activeDialog = null;
            }

            if (_lifecycleSmokeMode && _lifecycleSmokeDialogCloseRequested && IsShuttingDown)
            {
                _lifecycleSmokeDialogClosed = true;
            }
        }
    }

    private void CloseActiveDialogForShutdown()
    {
        Form? dialog = _activeDialog;
        if (dialog is null || dialog.IsDisposed)
        {
            return;
        }

        if (_lifecycleSmokeMode)
        {
            _lifecycleSmokeDialogCloseRequested = true;
        }

        dialog.DialogResult = DialogResult.Cancel;
        dialog.Close();
    }

    private void HandleSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteConnect)
        {
            PostToUi(() => _engine.HandleSafetyEvent(EffectEventKind.SessionLock));
        }
    }

    private void HandlePowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            PostToUi(() => _engine.HandleSafetyEvent(EffectEventKind.Suspend));
        }
    }

    private void HandleDisplaySettingsChanging(object? sender, EventArgs e) =>
        PostToUi(_engine.InvalidateCursorForDisplayChange);

    private void HandleDisplaySettingsChanged(object? sender, EventArgs e) =>
        PostToUi(_engine.InvalidateCursorForDisplayChange);

    private void HandleUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        PostToUi(() =>
        {
            _engine.InvalidateCursorForDisplayChange();
            _themeService.Refresh();
            RefreshBranding();
            _trayMenu.RefreshTheme();
            if (_activeDialog is { IsDisposed: false } dialog)
            {
                _themeService.ApplyTo(dialog);
            }
        });

    private void RefreshBranding()
    {
        Icon replacement = FoxMouseIconFactory.CreateApplicationIcon(_themeService.Colors);
        Icon previous = _applicationIcon;
        _applicationIcon = replacement;
        _notifyIcon.Icon = replacement;

        if (_activeDialog is { IsDisposed: false } dialog)
        {
            dialog.Icon = replacement;
            if (dialog is IThemeAwareBranding branded)
            {
                branded.RefreshBranding(_themeService.Colors);
            }
        }

        previous.Dispose();
    }

    private void PostToUi(Action action)
    {
        if (IsShuttingDown)
        {
            return;
        }

        try
        {
            // SystemEvents can arrive on arbitrary threads, including the UI
            // thread. Always queue through the captured WinForms context so
            // every callback has the same ordering and exception boundary.
            _uiContext.Post(static state =>
            {
                UiWorkItem workItem = (UiWorkItem)state!;
                if (workItem.Owner.IsShuttingDown)
                {
                    return;
                }

                try
                {
                    workItem.Action();
                }
                catch (Exception exception)
                {
                    workItem.Owner._logger.Error("system-event-callback-failed", exception);
                }
            }, new UiWorkItem(this, action));
        }
        catch (Exception exception)
        {
            _logger.Error("system-event-dispatch-failed", exception);
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Error("settings-save-failed", exception);
            if (!IsShuttingDown)
            {
                MessageBox.Show(
                    FoxMouse.Core.UiText.Get("SettingsDiskError"),
                    "FoxMouse",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    private void QueueExternalSettingsReload()
    {
        if (IsShuttingDown)
        {
            return;
        }

        _externalSettingsReloadPending = true;
        TryStartExternalSettingsOperation();
    }

    private void QueueExternalPreview()
    {
        if (IsShuttingDown)
        {
            return;
        }

        _externalPreviewPending = true;
        TryStartExternalSettingsOperation();
    }

    private void TryStartExternalSettingsOperation()
    {
        if (IsShuttingDown || _userOperationInProgress)
        {
            return;
        }

        if (_externalSettingsReloadPending)
        {
            _externalSettingsReloadPending = false;
            _userOperationTask = RunUserOperationAsync(ReloadExternalSettingsAsync);
            return;
        }

        if (_externalPreviewPending)
        {
            _externalPreviewPending = false;
            _userOperationTask = RunUserOperationAsync(PreviewExternalSettingsAsync);
        }
    }

    private Task PreviewExternalSettingsAsync()
    {
        _engine.Preview();
        _logger.Information("settings-preview-requested", "source=winui-settings-host");
        return Task.CompletedTask;
    }

    private async Task ReloadExternalSettingsAsync()
    {
        FoxMouseSettings reloaded = await _settingsStore.LoadAsync(_lifetimeCancellation.Token);
        _lifetimeCancellation.Token.ThrowIfCancellationRequested();

        _settings = reloaded;
        UiText.Configure(reloaded.Language);
        await _engine.ApplySettingsAsync(_settings, _lifetimeCancellation.Token);
        try
        {
            StartupRegistration.SetEnabled(
                _settings.StartWithWindows,
                ExecutableLocator.Current);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            _logger.Error("startup-registration-reload-failed", exception);
        }

        _logger.Information("settings-reloaded", "source=winui-settings-host");
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (IsShuttingDown)
        {
            return;
        }

        _notifyIcon.Text = $"FoxMouse — {_engine.StatusText}";
        _trayMenu.UpdateState(_settings.Enabled, _engine.StatusText);
    }

    private bool IsShuttingDown => Volatile.Read(ref _shutdownState) != 0;

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception)
        {
            // Cleanup is best-effort after the engine has completed its
            // cursor restoration protocol. Continue to release every owner.
        }
    }

    private async Task AwaitShutdownStepAsync(Task task, string eventName)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            _logger.Error(eventName, exception);
        }
    }

    private sealed record UiWorkItem(FoxMouseApplicationContext Owner, Action Action);
}
