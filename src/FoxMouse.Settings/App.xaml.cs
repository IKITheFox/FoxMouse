using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using FoxMouse.Platform.Windows.Configuration;

namespace FoxMouse.Settings;

public partial class App : Application
{
    private readonly CancellationTokenSource _activationCancellation = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Task _activationListener;
    private MainWindow? _window;
    private bool _windowSmokeScheduled;
    private Task? _shutdownListener;

    public App()
    {
        FoxMouse.Core.FoxMouseSettings initialSettings = new SettingsStore(Program.SettingsRootOverride)
            .LoadAsync().GetAwaiter().GetResult();
        string preferredLanguage = Program.LanguageOverride ?? initialSettings.Language;
        FoxMouse.Core.UiText.Configure(preferredLanguage);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
            FoxMouse.Core.UiLanguage.Resolve(preferredLanguage);
        InitializeComponent();
        UnhandledException += HandleUnhandledException;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        // Window smoke tests deliberately use a process-unique AppInstance so
        // they can validate a second Settings window while the user's real
        // Settings host is already running. Do not let that isolated host
        // compete for the production activation pipe.
        _activationListener = Program.WindowSmokeTest
            ? Task.CompletedTask
            : SettingsActivationChannel.ListenAsync(
                request => _dispatcherQueue.TryEnqueue(() => Activate(request)),
                _activationCancellation.Token);
    }

    internal nint MainWindowHandle => _window?.WindowHandle ?? 0;

    protected override void OnLaunched(LaunchActivatedEventArgs args) =>
        Activate(Program.InitialActivation);

    internal void Activate(SettingsActivationRequest request)
    {
        try
        {
            if (_window is null)
            {
                _window = new MainWindow(request);
                _window.Closed += Window_Closed;
                if (!Program.WindowSmokeTest || Program.InteractiveUiTest)
                {
                    _shutdownListener ??= SettingsShutdownChannel.ListenAsync(
                        new SettingsStore(Program.SettingsRootOverride).SettingsPath,
                        RequestCurrentWindowExitAsync,
                        _activationCancellation.Token);
                }
            }
            else
            {
                _window.NavigateTo(request.Page);
            }

            _window.ActivateAndBringToFront();
            if (_window.WindowHandle != 0)
            {
                _ = SettingsHostReadyChannel.SignalReadyAsync(
                    request.LaunchToken,
                    _activationCancellation.Token);
            }

            if (Program.WindowSmokeTest && !Program.InteractiveUiTest && !_windowSmokeScheduled)
            {
                _windowSmokeScheduled = true;
                _ = CloseAfterWindowSmokeAsync();
            }
        }
        catch (Exception exception)
        {
            if (Program.WindowSmokeTest) Console.Error.WriteLine(exception);
            SettingsStartupDiagnostics.Write(exception);
            throw;
        }
    }

    private static void HandleUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        SettingsStartupDiagnostics.Write(args.Exception);
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _window = null;
    }

    private Task RequestCurrentWindowExitAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_window is not null) await _window.RequestGlobalExitAsync();
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetResult();
        return completion.Task;
    }

    private async Task CloseAfterWindowSmokeAsync()
    {
        await Task.Delay(250).ConfigureAwait(false);
        _dispatcherQueue.TryEnqueue(() =>
        {
            _window?.Close();
            Exit();
        });
    }
}
