using System.Diagnostics;
using System.Text.Json;
using FoxMouse.Deployment;

namespace FoxMouse.Uninstall;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        CleanupStaleWorkerCopies();
        CommandLine commandLine = CommandLine.Parse(args);
        DeploymentPaths paths = ResolvePaths(commandLine);
        FoxMouse.Core.UiText.Configure(InstallationLanguage.ReadPreference(paths.SettingsRoot));
        EnsureWindowSmokeIsIsolated(commandLine);
        if (!string.IsNullOrWhiteSpace(commandLine.ContractOutputPath))
        {
            WriteContract(commandLine.ContractOutputPath);
            return 0;
        }

        if (commandLine.Detached)
        {
            SignalReadyAndWaitForParent(commandLine);
        }
        else if (RelaunchOutsideMutableRoot(paths, args, commandLine))
        {
            // The root GUI launcher is intentionally asynchronous. The detached
            // host owns completion and any requested result-file contract.
            return 0;
        }

        MaintenanceSessionResult sessionResult = RunMaintenance(paths, commandLine);
        int exitCode = sessionResult.ExitCode;
        string? cleanupStatusFile = null;
        string? cleanupStartError = null;
        bool shouldCleanCurrentHost = commandLine.Detached ||
            (sessionResult.Succeeded &&
             sessionResult.Operation == DeploymentOperation.Uninstall &&
             IsCurrentMaintenanceHost(paths));
        if (shouldCleanCurrentHost)
        {
            try
            {
                cleanupStatusFile = StartCleanupWorker(paths, commandLine);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FoxMouse cleanup worker failed to start: {exception.Message}");
                cleanupStartError = exception.Message;
                exitCode = 2;
            }
        }

        WriteResult(
            commandLine.ResultFile,
            exitCode,
            commandLine.Detached,
            cleanupStatusFile,
            cleanupStartError,
            sessionResult.Error?.Message);
        return exitCode;
    }

    private static MaintenanceSessionResult RunMaintenance(DeploymentPaths paths, CommandLine commandLine)
    {
        DeploymentEngine engine = new();
        if (commandLine.Quiet || commandLine.Action is not null)
        {
            try
            {
                DeploymentOutcome outcome = Execute(engine, paths, commandLine);
                Console.WriteLine($"FoxMouse {outcome.Operation} completed.");
                return new MaintenanceSessionResult(
                    Attempted: true,
                    Operation: outcome.Operation,
                    Succeeded: true,
                    ExitCode: 0,
                    Error: null);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                if (!commandLine.Quiet)
                {
                    MessageBox.Show(exception.Message, "FoxMouse Maintenance", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return new MaintenanceSessionResult(
                    Attempted: true,
                    Operation: ParseOperation(commandLine.Action),
                    Succeeded: false,
                    ExitCode: 1,
                    Error: exception);
            }
        }

        using MaintenanceWindow window = new(
            engine,
            paths,
            ProductVersion(),
            () => new CachedPackage(engine.GetCachedPackagePath(paths)),
            setupMode: false,
            keepMaintenanceHost: IsCurrentMaintenanceHost(paths),
            launchAfterInstall: !commandLine.NoLaunch);
        ConfigureWindowSmoke(window, commandLine);
        Application.Run(window);
        return window.SessionResult;
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
                // Isolated smoke driver acknowledges the same completion
                // button as a user; production waits for explicit input.
                ((Button)window.Controls.Find("CloseAction", true).Single()).PerformClick();
            }
        };
    }

    private static DeploymentOperation? ParseOperation(string? action) => action?.ToLowerInvariant() switch
    {
        "repair" => DeploymentOperation.Repair,
        "uninstall" => DeploymentOperation.Uninstall,
        _ => null,
    };

    private static DeploymentOperation? ParseWindowSmokeOperation(string scenario) =>
        scenario.ToLowerInvariant() switch
        {
            "idle" => null,
            "uninstall-confirmation" => null,
            "repair" => DeploymentOperation.Repair,
            "uninstall" => DeploymentOperation.Uninstall,
            _ => throw new ArgumentException($"Unknown maintenance window smoke scenario: {scenario}"),
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
                "Maintenance window smoke tests require isolated roots, no command action, and the explicit test opt-in.");
        }
    }

    private static DeploymentOutcome Execute(
        DeploymentEngine engine,
        DeploymentPaths paths,
        CommandLine commandLine)
    {
        if (string.Equals(commandLine.Action, "repair", StringComparison.OrdinalIgnoreCase))
        {
            string packagePath = engine.GetCachedPackagePath(paths);
            InstallationStatus status = engine.GetStatus(paths);
            return engine.InstallOrRepair(new InstallRequest
            {
                Paths = paths,
                PackagePath = packagePath,
                Version = status.Version ?? ProductVersion(),
                LaunchAfterInstall = !commandLine.NoLaunch,
            });
        }

        return engine.Uninstall(new UninstallRequest
        {
            Paths = paths,
            KeepSettings = commandLine.KeepSettings,
            KeepMaintenanceHost = IsCurrentMaintenanceHost(paths),
        });
    }

    private static DeploymentPaths ResolvePaths(CommandLine commandLine)
    {
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

        return InstallationLocator.ResolveForUninstall(
            defaults,
            commandLine.InstallRoot,
            Environment.ProcessPath);
    }

    private static bool RelaunchOutsideMutableRoot(
        DeploymentPaths paths,
        string[] args,
        CommandLine commandLine)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        string executablePath = Path.GetFullPath(executable);
        bool insideInstallRoot = IsPathInside(executablePath, paths.InstallRoot);
        // Repair replaces the persistent maintenance executable as well as the
        // application. Never keep that executable mapped while replacing it.
        bool insideMaintenanceRoot = IsPathInside(executablePath, paths.MaintenanceHostRoot);
        bool insideSettingsBeingDeleted = !commandLine.KeepSettings && IsPathInside(executablePath, paths.SettingsRoot);
        if (!insideInstallRoot && !insideMaintenanceRoot && !insideSettingsBeingDeleted)
        {
            return false;
        }

        string sourceDirectory = Path.GetDirectoryName(executablePath)!;
        string cleanupSource = Path.Combine(sourceDirectory, "FoxMouse.Cleanup.exe");
        if (!File.Exists(cleanupSource))
        {
            throw new FileNotFoundException("The FoxMouse cleanup companion is missing.", cleanupSource);
        }

        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"FoxMouse-Uninstall-Host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        string detachedExecutable = Path.Combine(temporaryRoot, "FoxMouse.Uninstall.exe");
        File.Copy(executablePath, detachedExecutable, overwrite: false);
        File.Copy(cleanupSource, Path.Combine(temporaryRoot, "FoxMouse.Cleanup.exe"), overwrite: false);

        string readyEventName = @"Local\FoxMouse.Uninstall.Ready." + Guid.NewGuid().ToString("N");
        using EventWaitHandle readyEvent = new(false, EventResetMode.ManualReset, readyEventName);
        ProcessStartInfo startInfo = new(detachedExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = temporaryRoot,
        };
        foreach (string argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (string.IsNullOrWhiteSpace(commandLine.InstallRoot))
        {
            AddPair(startInfo, "--install-root", paths.InstallRoot);
        }

        startInfo.ArgumentList.Add("--detached");
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--ready-event");
        startInfo.ArgumentList.Add(readyEventName);
        using Process child = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not launch the detached FoxMouse maintenance host.");
        if (!readyEvent.WaitOne(TimeSpan.FromSeconds(10)))
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            throw new TimeoutException("The detached FoxMouse maintenance host did not acknowledge startup.");
        }

        return true;
    }

    private static void SignalReadyAndWaitForParent(CommandLine commandLine)
    {
        if (commandLine.ParentProcessId is null || string.IsNullOrWhiteSpace(commandLine.ReadyEventName))
        {
            throw new ArgumentException("Detached maintenance requires a parent PID and ready event.");
        }

        using (EventWaitHandle ready = EventWaitHandle.OpenExisting(commandLine.ReadyEventName))
        {
            ready.Set();
        }

        try
        {
            using Process parent = Process.GetProcessById(commandLine.ParentProcessId.Value);
            if (!parent.WaitForExit(30_000))
            {
                throw new TimeoutException("The original FoxMouse uninstaller did not release the installation directory.");
            }
        }
        catch (ArgumentException)
        {
            // The parent exited before the detached host opened its process handle.
        }
    }

    private static string? StartCleanupWorker(DeploymentPaths paths, CommandLine commandLine)
    {
        string targetFile = Path.GetFullPath(Environment.ProcessPath ??
            throw new InvalidOperationException("The current maintenance executable path is unavailable."));
        string targetDirectory = Path.GetDirectoryName(targetFile)!;
        string companionFile = Path.Combine(targetDirectory, "FoxMouse.Cleanup.exe");
        if (!File.Exists(companionFile))
        {
            throw new FileNotFoundException("The FoxMouse cleanup companion is missing.", companionFile);
        }

        if (!IsAllowedCleanupTarget(paths, targetFile, targetDirectory))
        {
            throw new InvalidOperationException("The maintenance executable is outside the approved cleanup roots.");
        }

        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string? statusFile = string.IsNullOrWhiteSpace(commandLine.ResultFile)
            ? null
            : commandLine.KeepSettings
                ? Path.Combine(paths.SettingsRoot, $"cleanup-result-{Guid.NewGuid():N}.json")
                : Path.Combine(temporaryRoot, $"FoxMouse-CleanupResult-{Guid.NewGuid():N}.json");
        string workerRoot = Path.Combine(temporaryRoot, $"FoxMouse-CleanupWorker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workerRoot);
        string workerExecutable = Path.Combine(workerRoot, "FoxMouse.Cleanup.exe");
        File.Copy(companionFile, workerExecutable, overwrite: false);

        string readyEventName = @"Local\FoxMouse.Cleanup.Ready." + Guid.NewGuid().ToString("N");
        using EventWaitHandle readyEvent = new(false, EventResetMode.ManualReset, readyEventName);
        ProcessStartInfo startInfo = new(workerExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = temporaryRoot,
        };
        AddPair(startInfo, "--after-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddPair(startInfo, "--target-file", targetFile);
        AddPair(startInfo, "--companion-file", companionFile);
        AddPair(startInfo, "--target-directory", targetDirectory);
        AddPair(startInfo, "--ready-event", readyEventName);
        if (!string.IsNullOrWhiteSpace(statusFile))
        {
            AddPair(startInfo, "--status-file", statusFile);
        }

        if (!string.IsNullOrWhiteSpace(commandLine.TestRoot))
        {
            AddPair(startInfo, "--test-root", commandLine.TestRoot);
            AddPair(startInfo, "--test-registry-root", commandLine.TestRegistryRoot!);
        }

        Process? worker = null;
        try
        {
            worker = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not launch the FoxMouse cleanup worker.");
            if (!readyEvent.WaitOne(TimeSpan.FromSeconds(10)))
            {
                int? workerExitCode = worker.HasExited ? worker.ExitCode : null;
                if (!worker.HasExited)
                {
                    worker.Kill(entireProcessTree: true);
                    _ = worker.WaitForExit(5_000);
                }

                string? workerError = ReadCleanupError(statusFile);
                throw new InvalidOperationException(
                    $"The FoxMouse cleanup worker did not acknowledge startup (exit {workerExitCode?.ToString() ?? "running"})" +
                    (string.IsNullOrWhiteSpace(workerError) ? "." : $": {workerError}"));
            }

            return statusFile;
        }
        catch
        {
            if (File.Exists(workerExecutable))
            {
                File.Delete(workerExecutable);
            }

            if (Directory.Exists(workerRoot))
            {
                Directory.Delete(workerRoot, recursive: false);
            }

            throw;
        }
        finally
        {
            worker?.Dispose();
        }
    }

    private static void AddPair(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string? ReadCleanupError(string? statusFile)
    {
        if (string.IsNullOrWhiteSpace(statusFile) || !File.Exists(statusFile))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statusFile));
            return document.RootElement.TryGetProperty("error", out JsonElement value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsCurrentMaintenanceHost(DeploymentPaths paths) =>
        Environment.ProcessPath is string executable &&
        string.Equals(
            Path.GetFullPath(executable),
            Path.GetFullPath(paths.MaintenanceExecutable),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedCleanupTarget(
        DeploymentPaths paths,
        string targetFile,
        string targetDirectory)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        if (!string.Equals(
                Path.GetFullPath(targetFile),
                Path.Combine(directory, "FoxMouse.Uninstall.exe"),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(directory, Path.GetFullPath(paths.MaintenanceHostRoot), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        return string.Equals(Path.GetDirectoryName(directory), temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            HasGuidSuffix(Path.GetFileName(directory), "FoxMouse-Uninstall-Host-");
    }

    private static bool IsPathInside(string path, string root)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasGuidSuffix(string leaf, string prefix)
    {
        if (!leaf.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = leaf[prefix.Length..];
        return suffix.Length == 32 && suffix.All(Uri.IsHexDigit);
    }

    private static void CleanupStaleWorkerCopies()
    {
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        foreach (string directory in Directory.EnumerateDirectories(temporaryRoot, "FoxMouse-CleanupWorker-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                DirectoryInfo info = new(directory);
                if (!HasGuidSuffix(info.Name, "FoxMouse-CleanupWorker-") ||
                    (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1))
                {
                    continue;
                }

                FileSystemInfo[] entries = info.EnumerateFileSystemInfos().ToArray();
                if (entries.Length != 1 ||
                    entries[0] is not FileInfo workerFile ||
                    (workerFile.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !string.Equals(workerFile.Name, "FoxMouse.Cleanup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Unexpected stale cleanup worker contents.");
                }

                File.SetAttributes(workerFile.FullName, FileAttributes.Normal);
                File.Delete(workerFile.FullName);
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }
    }

    private static void WriteContract(string output)
    {
        string outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            schema = "foxmouse.maintenance/1",
            product = "FoxMouse",
            version = ProductVersion(),
            graphical = true,
            verbs = new[] { "repair", "uninstall" },
            arpScope = "HKCU",
            detachedHandshake = true,
            rootLauncherCompletion = "asynchronous-result-file",
            quietExitCode = "synchronous-maintenance-host",
            cleanup = "constrained-worker",
        }));
    }

    private static void WriteResult(
        string? resultFile,
        int exitCode,
        bool detached,
        string? cleanupStatusFile,
        string? cleanupStartError,
        string? operationError)
    {
        if (string.IsNullOrWhiteSpace(resultFile))
        {
            return;
        }

        string path = Path.GetFullPath(resultFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFilePublisher.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema = "foxmouse.maintenance-result/1",
            exitCode,
            completedUtc = DateTimeOffset.UtcNow,
            hostProcessId = Environment.ProcessId,
            temporaryHostRoot = detached && Environment.ProcessPath is string executable
                ? Path.GetDirectoryName(executable)
                : null,
            cleanupStatusFile,
            cleanupStartError,
            operationError,
        }));
    }

    private static string ProductVersion()
    {
        Version version = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private sealed record CommandLine(
        string? Action,
        bool Quiet,
        bool NoLaunch,
        bool KeepSettings,
        bool Detached,
        int? ParentProcessId,
        string? ReadyEventName,
        string? ContractOutputPath,
        string? ResultFile,
        string? InstallRoot,
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
            bool quiet = false;
            bool noLaunch = false;
            bool keepSettings = true;
            bool detached = false;
            int? parentProcessId = null;
            string? readyEventName = null;
            string? contractOutput = null;
            string? resultFile = null;
            string? installRoot = null;
            string? testRoot = null;
            string? testRegistryRoot = null;
            string? windowSmokeScenario = null;
            string? windowSmokeCapturePath = null;
            string? windowSmokeMetricsPath = null;
            bool windowSmokeRequestClose = false;
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                switch (argument.ToLowerInvariant())
                {
                    case "--repair":
                    case "--uninstall":
                        action = argument[2..];
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
                    case "--detached":
                        detached = true;
                        break;
                    case "--parent-pid" when index + 1 < args.Length:
                        parentProcessId = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--ready-event" when index + 1 < args.Length:
                        readyEventName = args[++index];
                        break;
                    case "--write-contract" when index + 1 < args.Length:
                        contractOutput = args[++index];
                        break;
                    case "--result-file" when index + 1 < args.Length:
                        resultFile = args[++index];
                        break;
                    case "--install-root" when index + 1 < args.Length:
                        installRoot = args[++index];
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
                        throw new ArgumentException($"Unknown maintenance argument: {argument}");
                }
            }

            return new CommandLine(
                action,
                quiet,
                noLaunch,
                keepSettings,
                detached,
                parentProcessId,
                readyEventName,
                contractOutput,
                resultFile,
                installRoot,
                testRoot,
                testRegistryRoot,
                windowSmokeScenario,
                windowSmokeCapturePath,
                windowSmokeMetricsPath,
                windowSmokeRequestClose);
        }
    }

    private sealed class CachedPackage(string packagePath) : IDisposablePackage
    {
        public string PackagePath { get; } = packagePath;

        public void Dispose()
        {
        }
    }
}
