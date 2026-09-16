using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FoxMouse.Settings;

public static class Program
{
    private const string InstanceKey = "FoxMouse.Settings.v1";
    private static DispatcherQueue? _dispatcherQueue;
    private static SettingsActivationRequest _initialActivation = SettingsActivationRequest.Default;

    internal static SettingsActivationRequest InitialActivation => _initialActivation;
    internal static bool WindowSmokeTest { get; private set; }
    internal static bool InteractiveUiTest { get; private set; }
    internal static string? SettingsRootOverride { get; private set; }
    internal static string? LanguageOverride { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        LanguageOverride = args.FirstOrDefault(value => value.StartsWith("--language=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1];
        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            return SettingsSmokeRunner.Run();
        }

        InteractiveUiTest = args.Contains("--interactive-ui-test", StringComparer.OrdinalIgnoreCase);
        WindowSmokeTest = InteractiveUiTest || args.Contains("--window-smoke-test", StringComparer.OrdinalIgnoreCase);
        SettingsRootOverride = WindowSmokeTest
            ? Path.Combine(
                Path.GetTempPath(),
                "FoxMouse.Settings.WindowSmoke",
                Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"))
            : null;
        if (InteractiveUiTest) Directory.CreateDirectory(SettingsRootOverride!);

        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppActivationArguments activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        _initialActivation = SettingsActivationRequest.Parse(string.Join(' ', args));

        string instanceKey = WindowSmokeTest
            ? $"{InstanceKey}.WindowSmoke.{Environment.ProcessId}"
            : InstanceKey;
        AppInstance mainInstance = AppInstance.FindOrRegisterForKey(instanceKey);
        if (!mainInstance.IsCurrent)
        {
            mainInstance.RedirectActivationToAsync(activationArguments)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            SettingsActivationChannel.NotifyAsync(_initialActivation)
                .GetAwaiter()
                .GetResult();
            return 0;
        }

        mainInstance.Activated += HandleRedirectedActivation;
        try
        {
            Application.Start(initializationCallbackParams =>
            {
                _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(_dispatcherQueue));
                _ = initializationCallbackParams;
                new App();
            });
            return 0;
        }
        finally
        {
            DeleteWindowSmokeRoot();
        }
    }

    private static void HandleRedirectedActivation(object? sender, AppActivationArguments args)
    {
        SettingsActivationRequest request = SettingsActivationRequest.FromActivationArguments(args);
        _dispatcherQueue?.TryEnqueue(() => ((App)Application.Current).Activate(request));
    }

    private static void DeleteWindowSmokeRoot()
    {
        // Interactive acceptance needs the saved file after the window exits
        // to distinguish save from discard. This is a unique temporary root,
        // never the user's production configuration.
        if (InteractiveUiTest) return;

        string? root = SettingsRootOverride;
        SettingsRootOverride = null;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FoxMouse.Settings.WindowSmoke"));
        string resolvedRoot = Path.GetFullPath(root);
        if (!resolvedRoot.StartsWith(
                parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
