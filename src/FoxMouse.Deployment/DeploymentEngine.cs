using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace FoxMouse.Deployment;

public sealed class DeploymentEngine
{
    private const string StartupValueName = "FoxMouse";
    private const string InstallStateFileName = ".foxmouse-install.json";
    private const int CurrentInstallStateSchemaVersion = 2;
    private const string ProductId = "FoxMouse.Desktop";
    private const string CachedPackageFileName = "FoxMouse-package.zip";
    private const string CachedHashFileName = "FoxMouse-package.sha256";

    public InstallationStatus GetStatus(DeploymentPaths paths)
    {
        DeploymentPaths normalized = paths.NormalizeAndValidate();
        string app = Path.Combine(normalized.InstallRoot, "FoxMouse.exe");
        if (!File.Exists(app))
        {
            return new InstallationStatus(InstallationKind.Absent, null);
        }

        string statePath = Path.Combine(normalized.InstallRoot, InstallStateFileName);
        if (!File.Exists(statePath))
        {
            string? legacyVersion = FileVersionInfo.GetVersionInfo(app).FileVersion;
            return new InstallationStatus(InstallationKind.Legacy, legacyVersion);
        }

        try
        {
            InstallState? state = ReadInstallState(statePath);
            return state is not null && IsValidInstallState(state, normalized.InstallRoot)
                ? new InstallationStatus(
                    InstallationKind.Managed,
                    state.Version,
                    state.SchemaVersion is 0 ? 1 : state.SchemaVersion)
                : new InstallationStatus(InstallationKind.Invalid, null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new InstallationStatus(InstallationKind.Invalid, null);
        }
    }

    public string GetCachedPackagePath(DeploymentPaths paths)
    {
        DeploymentPaths normalized = paths.NormalizeAndValidate();
        string package = Path.Combine(normalized.InstallerCacheRoot, CachedPackageFileName);
        string hashFile = Path.Combine(normalized.InstallerCacheRoot, CachedHashFileName);
        if (!File.Exists(package) || !File.Exists(hashFile))
        {
            throw new FileNotFoundException("The repair cache is unavailable. Run the current FoxMouse Setup again.");
        }

        string expectedHash = File.ReadAllText(hashFile).Trim();
        string actualHash = DeploymentFileSystem.ComputeSha256(package);
        InstallState? state = ReadInstallState(Path.Combine(normalized.InstallRoot, InstallStateFileName));
        if (state is null ||
            !IsValidInstallState(state, normalized.InstallRoot) ||
            !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.PackageSha256, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The repair cache is not bound to the active installation state.");
        }

        return package;
    }

    public DeploymentOutcome InstallOrRepair(InstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DeploymentPaths paths = request.Paths.NormalizeAndValidate();
        using DeploymentOperationLock operationLock = DeploymentOperationLock.Acquire(paths.InstallRoot);
        string packagePath = DeploymentFileSystem.FullPath(request.PackagePath);
        if (string.IsNullOrWhiteSpace(request.Version))
        {
            throw new ArgumentException("A product version is required.", nameof(request));
        }

        IProductProcessController processController = request.ProcessController ?? new FoxMouseProductProcessController();
        IShortcutWriter shortcutWriter = request.ShortcutWriter ?? new WindowsShortcutWriter();
        InstallationStatus previousStatus = GetStatus(paths);
        bool recognizedExistingInstall = IsRecognizedExistingInstall(paths, previousStatus);
        InstallLocationPolicy.ValidateOperationRoot(paths, recognizedExistingInstall);
        if (previousStatus.Kind == InstallationKind.Invalid)
        {
            throw new InvalidDataException("The FoxMouse installation state is invalid or belongs to another location.");
        }

        if (previousStatus.Kind == InstallationKind.Legacy && !recognizedExistingInstall)
        {
            throw new InvalidDataException("The existing folder is not a trusted legacy FoxMouse installation.");
        }

        DeploymentOperation operation = previousStatus.Kind switch
        {
            InstallationKind.Absent => DeploymentOperation.Install,
            InstallationKind.Managed when string.Equals(previousStatus.Version, request.Version, StringComparison.OrdinalIgnoreCase) => DeploymentOperation.Repair,
            _ => DeploymentOperation.Upgrade,
        };

        string transactionId = Guid.NewGuid().ToString("N");
        string installParent = Path.GetDirectoryName(paths.InstallRoot)!;
        string cacheParent = Path.GetDirectoryName(paths.InstallerCacheRoot)!;
        Directory.CreateDirectory(installParent);
        Directory.CreateDirectory(cacheParent);

        string candidateRoot = Path.Combine(installParent, $".FoxMouse.staging.{transactionId}");
        string backupRoot = Path.Combine(installParent, $".FoxMouse.backup.{transactionId}");
        string cacheCandidate = Path.Combine(cacheParent, $".InstallerCache.staging.{transactionId}");
        string cacheBackup = Path.Combine(cacheParent, $".InstallerCache.backup.{transactionId}");
        string transactionRoot = Path.Combine(Path.GetTempPath(), $"FoxMouse-Deployment-{transactionId}");
        string trustedPackagePath = Path.Combine(transactionRoot, "FoxMouse-package.zip");
        string shortcutBackup = Path.Combine(transactionRoot, "FoxMouse.previous.lnk");
        string maintenanceBackup = Path.Combine(transactionRoot, "FoxMouse.Uninstall.previous.exe");
        string cleanupBackup = Path.Combine(transactionRoot, "FoxMouse.Cleanup.previous.exe");

        bool previousAppWasRunning = processController.IsApplicationRunning(paths.InstallRoot);
        bool previousShortcutExisted = File.Exists(paths.StartMenuShortcut);
        RegistryKeyState arpState = RegistryKeyState.Capture(paths.UninstallRegistrySubKey);
        bool oldInstallBackedUp = false;
        bool newInstallActivated = false;
        bool oldCacheBackedUp = false;
        bool newCacheActivated = false;
        bool launchedNewVersion = false;
        bool previousMaintenanceHostExisted = File.Exists(paths.MaintenanceExecutable);
        bool previousCleanupHostExisted = File.Exists(paths.MaintenanceCleanupExecutable);
        bool maintenanceHostUpdated = false;
        bool committed = false;
        string? initialSettingsJson = null;

        try
        {
            Directory.CreateDirectory(transactionRoot);
            if (previousShortcutExisted)
            {
                File.Copy(paths.StartMenuShortcut, shortcutBackup, overwrite: false);
            }

            string trustedPackageHash = DeploymentFileSystem.CopyFileAndComputeSha256(packagePath, trustedPackagePath);
            ValidateCachedPackageBindingIfApplicable(paths, packagePath, trustedPackageHash);
            DeploymentFileSystem.ExtractProduct(trustedPackagePath, candidateRoot);
            InstallState state = new(
                request.Version,
                DateTimeOffset.UtcNow,
                trustedPackageHash,
                CurrentInstallStateSchemaVersion,
                ProductId,
                ResolveInstallId(paths),
                paths.InstallRoot);
            File.WriteAllText(
                Path.Combine(candidateRoot, InstallStateFileName),
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

            Directory.CreateDirectory(cacheCandidate);
            File.Copy(trustedPackagePath, Path.Combine(cacheCandidate, CachedPackageFileName), overwrite: false);
            File.WriteAllText(Path.Combine(cacheCandidate, CachedHashFileName), state.PackageSha256);
            request.Checkpoint?.Invoke("prepared");

            processController.StopSafely(
                paths.InstallRoot,
                Path.Combine(candidateRoot, "FoxMouse.Guard.exe"));

            if (Directory.Exists(paths.InstallRoot))
            {
                DeploymentFileSystem.MoveDirectory(paths.InstallRoot, backupRoot);
                oldInstallBackedUp = true;
            }

            DeploymentFileSystem.MoveDirectory(candidateRoot, paths.InstallRoot);
            newInstallActivated = true;

            if (previousMaintenanceHostExisted)
            {
                File.Copy(paths.MaintenanceExecutable, maintenanceBackup, overwrite: false);
            }
            if (previousCleanupHostExisted)
            {
                File.Copy(paths.MaintenanceCleanupExecutable, cleanupBackup, overwrite: false);
            }

            maintenanceHostUpdated = true;
            ReplaceMaintenanceHostFile(
                Path.Combine(paths.InstallRoot, "FoxMouse.Uninstall.exe"),
                paths.MaintenanceExecutable,
                transactionId);
            ReplaceMaintenanceHostFile(
                Path.Combine(paths.InstallRoot, "FoxMouse.Cleanup.exe"),
                paths.MaintenanceCleanupExecutable,
                transactionId);

            if (Directory.Exists(paths.InstallerCacheRoot))
            {
                DeploymentFileSystem.MoveDirectory(paths.InstallerCacheRoot, cacheBackup);
                oldCacheBackedUp = true;
            }

            DeploymentFileSystem.MoveDirectory(cacheCandidate, paths.InstallerCacheRoot);
            newCacheActivated = true;
            request.Checkpoint?.Invoke("files-activated");

            shortcutWriter.Create(
                paths.StartMenuShortcut,
                Path.Combine(paths.InstallRoot, "FoxMouse.exe"),
                paths.InstallRoot,
                "FoxMouse - shake to find your cursor");
            ArpRegistration.Write(paths, request.Version, request.Publisher, request.AboutUrl);
            request.Checkpoint?.Invoke("registered");

            if (operation == DeploymentOperation.Install)
                initialSettingsJson = InstallationLanguage.Initialize(paths.SettingsRoot, request.InitialLanguage);

            if (request.LaunchAfterInstall)
            {
                processController.StartApplication(paths.InstallRoot);
                launchedNewVersion = true;
            }

            request.Checkpoint?.Invoke("launched");
            committed = true;
            return new DeploymentOutcome(operation, request.Version, paths.InstallRoot);
        }
        catch (Exception exception)
        {
            List<string> rollbackFailures = [];
            if (launchedNewVersion && newInstallActivated)
            {
                TryRollback(
                    () => processController.StopSafely(paths.InstallRoot, Path.Combine(paths.InstallRoot, "FoxMouse.Guard.exe")),
                    "stop the failed new version",
                    rollbackFailures);
            }

            TryRollback(() => InstallationLanguage.Rollback(paths.SettingsRoot, initialSettingsJson),
                "restore initial language settings", rollbackFailures);

            if (newInstallActivated && Directory.Exists(paths.InstallRoot))
            {
                TryRollback(
                    () => DeploymentFileSystem.MoveDirectory(paths.InstallRoot, candidateRoot),
                    "quarantine the failed new installation",
                    rollbackFailures);
                newInstallActivated = Directory.Exists(paths.InstallRoot);
            }

            if (oldInstallBackedUp && Directory.Exists(backupRoot) && !Directory.Exists(paths.InstallRoot))
            {
                TryRollback(
                    () => DeploymentFileSystem.MoveDirectory(backupRoot, paths.InstallRoot),
                    "restore the previous installation",
                    rollbackFailures);
                oldInstallBackedUp = Directory.Exists(backupRoot);
            }

            if (newCacheActivated && Directory.Exists(paths.InstallerCacheRoot))
            {
                TryRollback(
                    () => DeploymentFileSystem.MoveDirectory(paths.InstallerCacheRoot, cacheCandidate),
                    "quarantine the failed repair cache",
                    rollbackFailures);
                newCacheActivated = Directory.Exists(paths.InstallerCacheRoot);
            }

            if (maintenanceHostUpdated)
            {
                TryRollback(
                    () => RestoreMaintenanceHost(
                        paths,
                        maintenanceBackup,
                        previousMaintenanceHostExisted,
                        cleanupBackup,
                        previousCleanupHostExisted),
                    "restore the maintenance host",
                    rollbackFailures);
            }

            if (oldCacheBackedUp && Directory.Exists(cacheBackup) && !Directory.Exists(paths.InstallerCacheRoot))
            {
                TryRollback(
                    () => DeploymentFileSystem.MoveDirectory(cacheBackup, paths.InstallerCacheRoot),
                    "restore the previous repair cache",
                    rollbackFailures);
                oldCacheBackedUp = Directory.Exists(cacheBackup);
            }

            TryRollback(() => arpState.Restore(paths.UninstallRegistrySubKey), "restore ARP registration", rollbackFailures);
            TryRollback(
                () => RestoreShortcut(paths.StartMenuShortcut, shortcutBackup, previousShortcutExisted),
                "restore the Start menu shortcut",
                rollbackFailures);

            if (previousAppWasRunning && Directory.Exists(paths.InstallRoot))
            {
                TryRollback(() => processController.StartApplication(paths.InstallRoot), "restart the previous version", rollbackFailures);
            }

            string message = $"FoxMouse {operation.ToString().ToLowerInvariant()} failed: {exception.Message}";
            if (rollbackFailures.Count > 0)
            {
                message += " Rollback also reported: " + string.Join("; ", rollbackFailures);
            }

            throw new DeploymentException(message, exception);
        }
        finally
        {
            if (committed)
            {
                DeleteTreeBestEffort(backupRoot);
                DeleteTreeBestEffort(cacheBackup);
            }

            DeleteTreeBestEffort(candidateRoot);
            DeleteTreeBestEffort(cacheCandidate);
            DeleteTreeBestEffort(transactionRoot);
        }
    }

    public DeploymentOutcome Uninstall(UninstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DeploymentPaths paths = request.Paths.NormalizeAndValidate();
        using DeploymentOperationLock operationLock = DeploymentOperationLock.Acquire(paths.InstallRoot);
        IProductProcessController processController = request.ProcessController ?? new FoxMouseProductProcessController();
        InstallationStatus previousStatus = GetStatus(paths);
        bool recognizedExistingInstall = IsRecognizedExistingInstall(paths, previousStatus);
        InstallLocationPolicy.ValidateOperationRoot(paths, recognizedExistingInstall);
        if (previousStatus.Kind == InstallationKind.Invalid ||
            (previousStatus.Kind == InstallationKind.Legacy && !recognizedExistingInstall))
        {
            throw new InvalidDataException("The selected folder is not a trusted FoxMouse installation and will not be removed.");
        }

        string version = previousStatus.Version ?? "unknown";
        string transactionId = Guid.NewGuid().ToString("N");
        string installParent = Path.GetDirectoryName(paths.InstallRoot)!;
        string settingsParent = Path.GetDirectoryName(paths.SettingsRoot)!;
        string cacheParent = Path.GetDirectoryName(paths.InstallerCacheRoot)!;
        string transactionRoot = Path.Combine(Path.GetTempPath(), $"FoxMouse-Uninstall-{transactionId}");
        string installQuarantine = Path.Combine(installParent, $".FoxMouse.uninstall.{transactionId}");
        string settingsQuarantine = Path.Combine(settingsParent, $".FoxMouse.settings-uninstall.{transactionId}");
        string cacheQuarantine = Path.Combine(cacheParent, $".InstallerCache.uninstall.{transactionId}");
        string shortcutBackup = Path.Combine(transactionRoot, "FoxMouse.previous.lnk");
        string maintenanceQuarantine = Path.Combine(settingsParent, $".FoxMouse.maintenance-uninstall.{transactionId}");

        bool previousAppWasRunning = processController.IsApplicationRunning(paths.InstallRoot);
        bool previousShortcutExisted = File.Exists(paths.StartMenuShortcut);
        RegistryKeyState arpState = RegistryKeyState.Capture(paths.UninstallRegistrySubKey);
        RegistryValueSnapshot startupState = RegistryValueSnapshot.Capture(paths.StartupRegistrySubKey, StartupValueName);
        bool installMoved = false;
        bool settingsMoved = false;
        bool cacheMoved = false;
        bool maintenanceMoved = false;
        bool committed = false;

        try
        {
            Directory.CreateDirectory(transactionRoot);
            if (previousShortcutExisted)
            {
                File.Copy(paths.StartMenuShortcut, shortcutBackup, overwrite: false);
            }

            if (Directory.Exists(paths.InstallRoot))
            {
                processController.StopSafely(paths.InstallRoot, Path.Combine(paths.InstallRoot, "FoxMouse.Guard.exe"));
                DeploymentFileSystem.MoveDirectory(paths.InstallRoot, installQuarantine);
                installMoved = true;
            }

            if (Directory.Exists(paths.InstallerCacheRoot))
            {
                DeploymentFileSystem.MoveDirectory(paths.InstallerCacheRoot, cacheQuarantine);
                cacheMoved = true;
            }

            if (request.KeepSettings &&
                !request.KeepMaintenanceHost &&
                Directory.Exists(paths.MaintenanceHostRoot))
            {
                DeploymentFileSystem.MoveDirectory(paths.MaintenanceHostRoot, maintenanceQuarantine);
                maintenanceMoved = true;
            }

            if (!request.KeepSettings && Directory.Exists(paths.SettingsRoot))
            {
                DeploymentFileSystem.MoveDirectory(paths.SettingsRoot, settingsQuarantine);
                settingsMoved = true;
            }

            ArpRegistration.Remove(paths);
            using (RegistryKey? startupKey = Registry.CurrentUser.OpenSubKey(paths.StartupRegistrySubKey, writable: true))
            {
                startupKey?.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }

            if (File.Exists(paths.StartMenuShortcut))
            {
                File.Delete(paths.StartMenuShortcut);
            }

            request.Checkpoint?.Invoke("unregistered");
            committed = true;
            return new DeploymentOutcome(DeploymentOperation.Uninstall, version, paths.InstallRoot);
        }
        catch (Exception exception)
        {
            List<string> rollbackFailures = [];
            if (maintenanceMoved && Directory.Exists(maintenanceQuarantine) && !Directory.Exists(paths.MaintenanceHostRoot))
            {
                TryRollback(
                    () => DeploymentFileSystem.MoveDirectory(maintenanceQuarantine, paths.MaintenanceHostRoot),
                    "restore maintenance host",
                    rollbackFailures);
                maintenanceMoved = Directory.Exists(maintenanceQuarantine);
            }

            if (settingsMoved && Directory.Exists(settingsQuarantine) && !Directory.Exists(paths.SettingsRoot))
            {
                TryRollback(
                    () => DeploymentFileSystem.MoveDirectory(settingsQuarantine, paths.SettingsRoot),
                    "restore settings",
                    rollbackFailures);
                settingsMoved = Directory.Exists(settingsQuarantine);
            }

            if (cacheMoved && Directory.Exists(cacheQuarantine) && !Directory.Exists(paths.InstallerCacheRoot))
            {
                TryRollback(
                    () => DeploymentFileSystem.MoveDirectory(cacheQuarantine, paths.InstallerCacheRoot),
                    "restore repair cache",
                    rollbackFailures);
                cacheMoved = Directory.Exists(cacheQuarantine);
            }

            if (installMoved && Directory.Exists(installQuarantine) && !Directory.Exists(paths.InstallRoot))
            {
                TryRollback(
                    () => DeploymentFileSystem.MoveDirectory(installQuarantine, paths.InstallRoot),
                    "restore installation",
                    rollbackFailures);
                installMoved = Directory.Exists(installQuarantine);
            }

            TryRollback(() => arpState.Restore(paths.UninstallRegistrySubKey), "restore ARP registration", rollbackFailures);
            TryRollback(() => startupState.Restore(paths.StartupRegistrySubKey, StartupValueName), "restore startup registration", rollbackFailures);
            TryRollback(
                () => RestoreShortcut(paths.StartMenuShortcut, shortcutBackup, previousShortcutExisted),
                "restore the Start menu shortcut",
                rollbackFailures);

            if (previousAppWasRunning && Directory.Exists(paths.InstallRoot))
            {
                TryRollback(() => processController.StartApplication(paths.InstallRoot), "restart FoxMouse", rollbackFailures);
            }

            string message = $"FoxMouse uninstall failed: {exception.Message}";
            if (rollbackFailures.Count > 0)
            {
                message += " Rollback also reported: " + string.Join("; ", rollbackFailures);
            }

            throw new DeploymentException(message, exception);
        }
        finally
        {
            if (committed)
            {
                DeleteTreeBestEffort(installQuarantine);
                DeleteTreeBestEffort(settingsQuarantine);
                DeleteTreeBestEffort(cacheQuarantine);
                DeleteTreeBestEffort(maintenanceQuarantine);
            }

            DeleteTreeBestEffort(transactionRoot);
        }
    }

    private static void RestoreShortcut(string shortcutPath, string backupPath, bool previouslyExisted)
    {
        if (previouslyExisted)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.Copy(backupPath, shortcutPath, overwrite: true);
        }
        else if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }

    private static void ReplaceMaintenanceHostFile(string source, string destination, string transactionId)
    {
        string root = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(root);
        DeploymentFileSystem.EnsureNoReparsePoints(root);
        string candidate = Path.Combine(root, $".FoxMouse.Uninstall.{transactionId}.new");
        try
        {
            File.Copy(source, candidate, overwrite: false);
            File.Move(candidate, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private static void RestoreMaintenanceHost(
        DeploymentPaths paths,
        string backupPath,
        bool previouslyExisted,
        string cleanupBackupPath,
        bool cleanupPreviouslyExisted)
    {
        RestoreMaintenanceHostFile(paths.MaintenanceExecutable, backupPath, previouslyExisted);
        RestoreMaintenanceHostFile(paths.MaintenanceCleanupExecutable, cleanupBackupPath, cleanupPreviouslyExisted);

        if (Directory.Exists(paths.MaintenanceHostRoot) &&
            !Directory.EnumerateFileSystemEntries(paths.MaintenanceHostRoot).Any())
        {
            Directory.Delete(paths.MaintenanceHostRoot);
        }
    }

    private static void RestoreMaintenanceHostFile(string destination, string backup, bool previouslyExisted)
    {
        if (previouslyExisted)
        {
            ReplaceMaintenanceHostFile(backup, destination, Guid.NewGuid().ToString("N"));
        }
        else if (File.Exists(destination))
        {
            File.Delete(destination);
        }
    }

    private static void ValidateCachedPackageBindingIfApplicable(
        DeploymentPaths paths,
        string sourcePackagePath,
        string copiedPackageHash)
    {
        string cachedPackage = DeploymentFileSystem.FullPath(Path.Combine(paths.InstallerCacheRoot, CachedPackageFileName));
        if (!string.Equals(DeploymentFileSystem.FullPath(sourcePackagePath), cachedPackage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string hashPath = Path.Combine(paths.InstallerCacheRoot, CachedHashFileName);
        string statePath = Path.Combine(paths.InstallRoot, InstallStateFileName);
        if (!File.Exists(hashPath) || !File.Exists(statePath))
        {
            throw new InvalidDataException("The repair cache has no matching installation state.");
        }

        string cachedHash = File.ReadAllText(hashPath).Trim();
        InstallState? state = ReadInstallState(statePath);
        if (state is null ||
            !IsValidInstallState(state, paths.InstallRoot) ||
            !string.Equals(cachedHash, copiedPackageHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.PackageSha256, copiedPackageHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The repair cache is not bound to the active installation state.");
        }
    }

    private static InstallState? ReadInstallState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(statePath));
    }

    private static string ResolveInstallId(DeploymentPaths paths)
    {
        try
        {
            InstallState? state = ReadInstallState(Path.Combine(paths.InstallRoot, InstallStateFileName));
            if (state is not null &&
                IsValidInstallState(state, paths.InstallRoot) &&
                Guid.TryParseExact(state.InstallId, "N", out _))
            {
                return state.InstallId!;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return Guid.NewGuid().ToString("N");
    }

    private static bool IsValidInstallState(InstallState state, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(state.Version) ||
            state.InstalledUtc == default ||
            state.PackageSha256 is null ||
            state.PackageSha256.Length != 64 ||
            !state.PackageSha256.All(Uri.IsHexDigit))
        {
            return false;
        }

        // v0.4.1 and earlier wrote no schema or root identity. Accept that
        // shape only as a migration source; the next repair writes schema v2.
        if (state.SchemaVersion is 0 or 1)
        {
            return true;
        }

        if (state.SchemaVersion != CurrentInstallStateSchemaVersion ||
            !string.Equals(state.ProductId, ProductId, StringComparison.Ordinal) ||
            !Guid.TryParseExact(state.InstallId, "N", out _) ||
            string.IsNullOrWhiteSpace(state.InstallRoot))
        {
            return false;
        }

        try
        {
            return string.Equals(
                DeploymentFileSystem.FullPath(state.InstallRoot),
                DeploymentFileSystem.FullPath(installRoot),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsRecognizedExistingInstall(DeploymentPaths paths, InstallationStatus status)
    {
        if (status.Kind == InstallationKind.Managed &&
            status.StateSchemaVersion >= CurrentInstallStateSchemaVersion)
        {
            return true;
        }

        if (status.Kind is not (InstallationKind.Legacy or InstallationKind.Managed))
        {
            return false;
        }

        if (paths.IsIsolated)
        {
            return true;
        }

        string productionDefault = DeploymentPaths.ForCurrentUser().InstallRoot;
        if (string.Equals(paths.InstallRoot, productionDefault, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        IReadOnlyDictionary<string, object?> arp = ArpRegistration.ReadValues(paths);
        return arp.TryGetValue("InstallLocation", out object? value) &&
            value is string location &&
            string.Equals(
                DeploymentFileSystem.FullPath(location),
                paths.InstallRoot,
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(paths.InstallRoot, "FoxMouse.Uninstall.exe"));
    }

    private static void TryRollback(Action action, string label, ICollection<string> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add($"could not {label}: {exception.Message}");
        }
    }

    private static void DeleteTreeBestEffort(string path)
    {
        try
        {
            DeploymentFileSystem.DeleteTree(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record InstallState(
        string Version,
        DateTimeOffset InstalledUtc,
        string PackageSha256,
        int SchemaVersion = 1,
        string? ProductId = null,
        string? InstallId = null,
        string? InstallRoot = null);
}
