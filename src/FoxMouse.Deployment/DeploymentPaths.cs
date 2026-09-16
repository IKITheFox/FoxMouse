namespace FoxMouse.Deployment;

public sealed record DeploymentPaths(
    string InstallRoot,
    string SettingsRoot,
    string InstallerCacheRoot,
    string StartMenuShortcut,
    string UninstallRegistrySubKey,
    string StartupRegistrySubKey)
{
    public const string ProductionUninstallRegistrySubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FoxMouse";

    public const string ProductionStartupRegistrySubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string MaintenanceHostRoot => Path.Combine(SettingsRoot, "Maintenance");

    public string MaintenanceExecutable => Path.Combine(MaintenanceHostRoot, "FoxMouse.Uninstall.exe");

    public string MaintenanceCleanupExecutable => Path.Combine(MaintenanceHostRoot, "FoxMouse.Cleanup.exe");

    /// <summary>
    /// Gets the filesystem boundary used by the isolated installer lifecycle tests.
    /// Production path sets leave this value unset.
    /// </summary>
    public string? IsolationRoot { get; init; }

    public bool IsIsolated => !string.IsNullOrWhiteSpace(IsolationRoot);

    public static DeploymentPaths ForCurrentUser()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return new DeploymentPaths(
            Path.Combine(localAppData, "Programs", "FoxMouse"),
            Path.Combine(localAppData, "FoxMouse"),
            Path.Combine(localAppData, "FoxMouse", "InstallerCache"),
            Path.Combine(programs, "FoxMouse.lnk"),
            ProductionUninstallRegistrySubKey,
            ProductionStartupRegistrySubKey);
    }

    /// <summary>
    /// Returns a copy of this path set that targets a different FoxMouse
    /// installation directory. Per-user settings, cache, shortcuts and registry
    /// locations intentionally remain stable.
    /// </summary>
    public DeploymentPaths WithInstallRoot(string installRoot)
    {
        string normalized = DeploymentFileSystem.FullPath(installRoot);
        DeploymentFileSystem.RequireSafeLeaf(normalized, "install root");
        if (!string.Equals(Path.GetFileName(normalized), InstallLocationPolicy.ProductDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The FoxMouse install root must end with '{InstallLocationPolicy.ProductDirectoryName}'.",
                nameof(installRoot));
        }

        return (this with { InstallRoot = normalized }).NormalizeAndValidate();
    }

    public static DeploymentPaths ForIsolatedTestRoot(string root, string registryRoot)
    {
        string temporaryRoot = DeploymentFileSystem.FullPath(Path.GetTempPath());
        string isolatedRoot = DeploymentFileSystem.FullPath(root);
        string prefix = temporaryRoot + Path.DirectorySeparatorChar;
        if (!isolatedRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(isolatedRoot).StartsWith("FoxMouse-InstallerLifecycle-", StringComparison.Ordinal) ||
            !registryRoot.StartsWith(@"Software\FoxMouse\Tests\InstallerLifecycle-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Isolated installer tests must use the FoxMouse temporary filesystem and HKCU test namespaces.");
        }

        if (Directory.Exists(isolatedRoot))
        {
            DeploymentFileSystem.EnsureNoReparsePoints(isolatedRoot);
        }

        return new DeploymentPaths(
            Path.Combine(isolatedRoot, "Programs", "FoxMouse"),
            Path.Combine(isolatedRoot, "LocalAppData", "FoxMouse"),
            Path.Combine(isolatedRoot, "LocalAppData", "FoxMouse", "InstallerCache"),
            Path.Combine(isolatedRoot, "StartMenu", "FoxMouse.lnk"),
            registryRoot + @"\Uninstall\FoxMouse",
            registryRoot + @"\Run")
        {
            IsolationRoot = isolatedRoot,
        };
    }

    internal DeploymentPaths NormalizeAndValidate()
    {
        string installRoot = DeploymentFileSystem.FullPath(InstallRoot);
        string settingsRoot = DeploymentFileSystem.FullPath(SettingsRoot);
        string cacheRoot = DeploymentFileSystem.FullPath(InstallerCacheRoot);
        string shortcut = DeploymentFileSystem.FullPath(StartMenuShortcut);
        string? isolationRoot = string.IsNullOrWhiteSpace(IsolationRoot)
            ? null
            : DeploymentFileSystem.FullPath(IsolationRoot);

        DeploymentFileSystem.RequireSafeLeaf(installRoot, "install root");
        DeploymentFileSystem.RequireSafeLeaf(settingsRoot, "settings root");
        DeploymentFileSystem.RequireSafeLeaf(cacheRoot, "installer cache root");
        DeploymentFileSystem.RequireSafeLeaf(shortcut, "Start menu shortcut");
        ValidateRegistrySubKey(UninstallRegistrySubKey, nameof(UninstallRegistrySubKey));
        ValidateRegistrySubKey(StartupRegistrySubKey, nameof(StartupRegistrySubKey));

        if (isolationRoot is not null)
        {
            DeploymentFileSystem.RequireSafeLeaf(isolationRoot, "isolation root");
            foreach ((string path, string label) in new[]
            {
                (installRoot, "install root"),
                (settingsRoot, "settings root"),
                (cacheRoot, "installer cache root"),
                (shortcut, "Start menu shortcut"),
            })
            {
                if (!InstallLocationPolicy.IsPathWithin(path, isolationRoot))
                {
                    throw new InvalidOperationException($"The isolated {label} escapes its test root.");
                }
            }
        }

        return this with
        {
            InstallRoot = installRoot,
            SettingsRoot = settingsRoot,
            InstallerCacheRoot = cacheRoot,
            StartMenuShortcut = shortcut,
            IsolationRoot = isolationRoot,
        };
    }

    private static void ValidateRegistrySubKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith('\\') ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("Registry subkeys must be relative HKCU paths.", parameterName);
        }
    }
}
