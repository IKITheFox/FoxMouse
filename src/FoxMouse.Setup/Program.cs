using System.Reflection;
using FoxMouse.Deployment;

namespace FoxMouse.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        DeploymentEngine engine = new();
        CommandLine commandLine = CommandLine.Parse(args);
        DeploymentPaths paths = ResolvePaths(commandLine);
        EnsureWindowSmokeIsIsolated(commandLine);
        string version = ProductVersion();

        if (!string.IsNullOrWhiteSpace(commandLine.ExtractPackagePath))
        {
            try
            {
                using PayloadPackage package = PayloadPackage.Open(commandLine.PackagePath);
                string destination = Path.GetFullPath(commandLine.ExtractPackagePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(package.PackagePath, destination, overwrite: false);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        if (commandLine.Quiet || commandLine.Action is not null)
        {
            try
            {
                DeploymentOutcome outcome = Execute(engine, paths, version, commandLine);
                Console.WriteLine($"FoxMouse {outcome.Operation} completed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                if (!commandLine.Quiet)
                {
                    MessageBox.Show(exception.Message, "FoxMouse Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return 1;
            }
        }

        using MaintenanceWindow window = new(
            engine,
            paths,
            version,
            () => PayloadPackage.Open(commandLine.PackagePath),
            setupMode: true,
            launchAfterInstall: !commandLine.NoLaunch);
        ConfigureWindowSmoke(window, commandLine);
        Application.Run(window);
        return window.SessionResult.ExitCode;
    }

    private static void ConfigureWindowSmoke(MaintenanceWindow window, CommandLine commandLine)
    {
        if (commandLine.WindowSmokeScenario is null)
        {
            return;
        }

        window.Shown += async (_, _) =>
        {
            await Task.Yield();
            string scenario = commandLine.WindowSmokeScenario;
            if (string.Equals(scenario, "uninstall-confirmation", StringComparison.Ordinal))
            {
                window.ShowUninstallConfirmationForTesting();
                await Task.Yield();
            }

            MaintenanceWindowEvidence.Capture(
                window,
                scenario,
                commandLine.WindowSmokeCapturePath,
                commandLine.WindowSmokeMetricsPath);

            DeploymentOperation? operation = ParseWindowSmokeOperation(scenario);
            if (operation is null)
            {
                window.RequestClose();
                return;
            }

            Task running = window.BeginAutomatedOperationForTestingAsync(operation.Value);
            if (commandLine.WindowSmokeRequestClose)
            {
                window.RequestClose();
            }

            await running.ConfigureAwait(true);
            if (!window.IsDisposed && window.SessionResult.Succeeded)
            {
                ((Button)window.Controls.Find("CloseAction", true).Single()).PerformClick();
            }
        };
    }

    private static DeploymentOperation? ParseWindowSmokeOperation(string scenario) =>
        scenario.ToLowerInvariant() switch
        {
            "idle" => null,
            "uninstall-confirmation" => null,
            "install" => DeploymentOperation.Install,
            "repair" => DeploymentOperation.Repair,
            "upgrade" => DeploymentOperation.Upgrade,
            "uninstall" => DeploymentOperation.Uninstall,
            _ => throw new ArgumentException($"Unknown Setup window smoke scenario: {scenario}"),
        };

    private static void EnsureWindowSmokeIsIsolated(CommandLine commandLine)
    {
        if (commandLine.WindowSmokeScenario is null &&
            commandLine.WindowSmokeCapturePath is null &&
            commandLine.WindowSmokeMetricsPath is null &&
            !commandLine.WindowSmokeRequestClose)
        {
            return;
        }

        if (!string.Equals(
                Environment.GetEnvironmentVariable("FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS"),
                "1",
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(commandLine.TestRoot) ||
            string.IsNullOrWhiteSpace(commandLine.TestRegistryRoot) ||
            string.IsNullOrWhiteSpace(commandLine.WindowSmokeScenario) ||
            commandLine.Action is not null ||
            commandLine.Quiet ||
            (commandLine.WindowSmokeRequestClose && commandLine.WindowSmokeScenario == "idle"))
        {
            throw new InvalidOperationException(
                "Installer window smoke tests require isolated roots, no command action, and the explicit test opt-in.");
        }
    }

    private static DeploymentOutcome Execute(
        DeploymentEngine engine,
        DeploymentPaths paths,
        string version,
        CommandLine commandLine)
    {
        if (string.Equals(commandLine.Action, "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return engine.Uninstall(new UninstallRequest
            {
                Paths = paths,
                KeepSettings = commandLine.KeepSettings,
            });
        }

        using PayloadPackage package = PayloadPackage.Open(commandLine.PackagePath);
        return engine.InstallOrRepair(new InstallRequest
        {
            Paths = paths,
            PackagePath = package.PackagePath,
            Version = version,
            LaunchAfterInstall = !commandLine.NoLaunch,
            InitialLanguage = FoxMouse.Core.UiText.Language,
        });
    }

    private static string ProductVersion()
    {
        Version version = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static DeploymentPaths ResolvePaths(CommandLine commandLine)
    {
        if (!string.IsNullOrWhiteSpace(commandLine.InstallParent) &&
            (string.Equals(commandLine.Action, "repair", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(commandLine.Action, "uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("--install-parent is valid only for a new installation.");
        }

        DeploymentPaths defaults;
        if (string.IsNullOrWhiteSpace(commandLine.TestRoot) &&
            string.IsNullOrWhiteSpace(commandLine.TestRegistryRoot))
        {
            defaults = DeploymentPaths.ForCurrentUser();
        }
        else
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS"),
                    "1",
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(commandLine.TestRoot) ||
                string.IsNullOrWhiteSpace(commandLine.TestRegistryRoot))
            {
                throw new InvalidOperationException("Isolated deployment paths require the explicit test environment opt-in.");
            }

            defaults = DeploymentPaths.ForIsolatedTestRoot(commandLine.TestRoot, commandLine.TestRegistryRoot);
        }

        return InstallationLocator.ResolveForSetup(
            defaults,
            commandLine.InstallParent,
            Environment.ProcessPath);
    }

    private sealed record CommandLine(
        string? Action,
        string? PackagePath,
        string? ExtractPackagePath,
        bool Quiet,
        bool NoLaunch,
        bool KeepSettings,
        string? InstallParent,
        string? TestRoot,
        string? TestRegistryRoot,
        string? WindowSmokeScenario,
        string? WindowSmokeCapturePath,
        string? WindowSmokeMetricsPath,
        bool WindowSmokeRequestClose)
    {
        internal static CommandLine Parse(string[] args)
        {
            string? action = null;
            string? package = null;
            string? extractPackage = null;
            bool quiet = false;
            bool noLaunch = false;
            bool keepSettings = true;
            string? installParent = null;
            string? testRoot = null;
            string? testRegistryRoot = null;
            string? windowSmokeScenario = null;
            string? windowSmokeCapturePath = null;
            string? windowSmokeMetricsPath = null;
            bool windowSmokeRequestClose = false;
            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "--language" when index + 1 < args.Length:
                        FoxMouse.Core.UiText.Configure(args[++index]);
                        break;
                    case "--install":
                    case "--repair":
                    case "--uninstall":
                        action = args[index][2..];
                        break;
                    case "--package" when index + 1 < args.Length:
                        package = args[++index];
                        break;
                    case "--extract-package" when index + 1 < args.Length:
                        extractPackage = args[++index];
                        break;
                    case "--quiet":
                        quiet = true;
                        break;
                    case "--no-launch":
                        noLaunch = true;
                        break;
                    case "--delete-settings":
                        keepSettings = false;
                        break;
                    case "--install-parent" when index + 1 < args.Length:
                        installParent = args[++index];
                        break;
                    case "--test-root" when index + 1 < args.Length:
                        testRoot = args[++index];
                        break;
                    case "--test-registry-root" when index + 1 < args.Length:
                        testRegistryRoot = args[++index];
                        break;
                    case "--window-smoke-test" when index + 1 < args.Length:
                        windowSmokeScenario = args[++index].ToLowerInvariant();
                        _ = ParseWindowSmokeOperation(windowSmokeScenario);
                        break;
                    case "--window-smoke-capture" when index + 1 < args.Length:
                        windowSmokeCapturePath = args[++index];
                        break;
                    case "--window-smoke-metrics" when index + 1 < args.Length:
                        windowSmokeMetricsPath = args[++index];
                        break;
                    case "--window-smoke-request-close":
                        windowSmokeRequestClose = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown Setup argument: {args[index]}");
                }
            }

            return new CommandLine(
                action,
                package,
                extractPackage,
                quiet,
                noLaunch,
                keepSettings,
                installParent,
                testRoot,
                testRegistryRoot,
                windowSmokeScenario,
                windowSmokeCapturePath,
                windowSmokeMetricsPath,
                windowSmokeRequestClose);
        }
    }

    private sealed class PayloadPackage : IDisposablePackage
    {
        private readonly string? _temporaryRoot;

        private PayloadPackage(string packagePath, string? temporaryRoot)
        {
            PackagePath = packagePath;
            _temporaryRoot = temporaryRoot;
        }

        public string PackagePath { get; }

        internal static PayloadPackage Open(string? externalPath)
        {
            if (!string.IsNullOrWhiteSpace(externalPath))
            {
                return new PayloadPackage(Path.GetFullPath(externalPath), null);
            }

            Assembly assembly = typeof(Program).Assembly;
            using Stream? resource = assembly.GetManifestResourceStream("FoxMouse.Payload.zip");
            if (resource is null)
            {
                throw new FileNotFoundException("This developer build has no embedded package. Pass --package <zip>.");
            }

            string temporaryRoot = Path.Combine(Path.GetTempPath(), $"FoxMouse-Setup-Payload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryRoot);
            string packagePath = Path.Combine(temporaryRoot, "FoxMouse-package.zip");
            using FileStream destination = new(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            resource.CopyTo(destination);
            return new PayloadPackage(packagePath, temporaryRoot);
        }

        public void Dispose()
        {
            if (_temporaryRoot is null)
            {
                return;
            }

            try
            {
                Directory.Delete(_temporaryRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
