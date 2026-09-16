using System.IO.Compression;
using System.Text.Json;
using FoxMouse.Deployment;
using Microsoft.Win32;

namespace FoxMouse.Deployment.Tests;

[Collection(DeploymentOperationCollection.Name)]
public sealed class InstallationLocatorTests : IDisposable
{
    private readonly string _testId = Guid.NewGuid().ToString("N");
    private readonly string _root;
    private readonly string _registryRoot;
    private readonly DeploymentPaths _defaults;

    public InstallationLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"FoxMouse-InstallerLifecycle-{_testId}");
        _registryRoot = $@"Software\FoxMouse\Tests\InstallerLifecycle-{_testId}";
        Directory.CreateDirectory(_root);
        _defaults = DeploymentPaths.ForIsolatedTestRoot(_root, _registryRoot);
    }

    [Fact]
    public void RegisteredCustomRootIsUsedBySetupAndUninstall()
    {
        string customRoot = Path.Combine(_root, "CustomApps", "FoxMouse");
        DeploymentPaths custom = _defaults.WithInstallRoot(customRoot);
        CreateManagedRoot(customRoot);
        ArpRegistration.Write(custom, "0.4.2", "FoxMouse Project", null);

        DeploymentPaths setup = InstallationLocator.ResolveForSetup(_defaults);
        DeploymentPaths uninstall = InstallationLocator.ResolveForUninstall(_defaults);

        Assert.Equal(customRoot, setup.InstallRoot, ignoreCase: true);
        Assert.Equal(customRoot, uninstall.InstallRoot, ignoreCase: true);
    }

    [Fact]
    public void ExistingInstallCannotBeRelocatedByParentArgument()
    {
        CreateManagedRoot(_defaults.InstallRoot);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            InstallationLocator.ResolveForSetup(_defaults, Path.Combine(_root, "Elsewhere")));

        Assert.Contains("already installed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunningRootUninstallerIsAuthoritativeWithoutArp()
    {
        string customRoot = Path.Combine(_root, "Self", "FoxMouse");
        CreateManagedRoot(customRoot);
        string uninstaller = Path.Combine(customRoot, "FoxMouse.Uninstall.exe");

        DeploymentPaths result = InstallationLocator.ResolveForUninstall(
            _defaults,
            currentExecutable: uninstaller);

        Assert.Equal(customRoot, result.InstallRoot, ignoreCase: true);
    }

    [Fact]
    public void HistoricalDefaultLegacyRootRemainsDiscoverable()
    {
        Directory.CreateDirectory(_defaults.InstallRoot);
        File.WriteAllText(Path.Combine(_defaults.InstallRoot, "FoxMouse.exe"), "legacy");

        DeploymentPaths result = InstallationLocator.ResolveForSetup(_defaults);

        Assert.Equal(_defaults.InstallRoot, result.InstallRoot, ignoreCase: true);
        Assert.Equal(InstallationKind.Legacy, new DeploymentEngine().GetStatus(result).Kind);
    }

    [Fact]
    public void VersionOneInstallStateIsDiscoveredAndMigratedToRootBoundSchemaV2()
    {
        Directory.CreateDirectory(_defaults.InstallRoot);
        File.WriteAllText(Path.Combine(_defaults.InstallRoot, "FoxMouse.exe"), "v1");
        File.WriteAllText(
            Path.Combine(_defaults.InstallRoot, ".foxmouse-install.json"),
            JsonSerializer.Serialize(new
            {
                Version = "0.4.2",
                InstalledUtc = DateTimeOffset.UtcNow,
                PackageSha256 = new string('b', 64),
            }));

        DeploymentPaths discovered = InstallationLocator.ResolveForSetup(_defaults);
        InstallationStatus oldStatus = new DeploymentEngine().GetStatus(discovered);
        Assert.Equal(InstallationKind.Managed, oldStatus.Kind);
        Assert.Equal(1, oldStatus.StateSchemaVersion);

        new DeploymentEngine().InstallOrRepair(new InstallRequest
        {
            Paths = discovered,
            PackagePath = CreatePackage(),
            Version = "0.4.2",
            LaunchAfterInstall = false,
            ProcessController = new FakeProcessController(),
            ShortcutWriter = new FakeShortcutWriter(),
        });

        InstallationStatus migrated = new DeploymentEngine().GetStatus(discovered);
        Assert.Equal(InstallationKind.Managed, migrated.Kind);
        Assert.Equal(2, migrated.StateSchemaVersion);
    }

    [Fact]
    public void TamperedRegisteredForeignRootIsRejectedAndUntouched()
    {
        string foreignRoot = Path.Combine(_root, "Foreign", "FoxMouse");
        string sentinel = Path.Combine(foreignRoot, "personal.txt");
        Directory.CreateDirectory(foreignRoot);
        File.WriteAllText(sentinel, "keep");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_defaults.UninstallRegistrySubKey, writable: true))
        {
            key.SetValue("DisplayName", "FoxMouse");
            key.SetValue("InstallLocation", foreignRoot);
            key.SetValue("UninstallString", $"\"{Path.Combine(foreignRoot, "FoxMouse.Uninstall.exe")}\"");
        }

        Assert.Throws<InvalidDataException>(() => InstallationLocator.ResolveForUninstall(_defaults));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void SchemaV2StateCannotBeCopiedToASecondInstallRoot()
    {
        string source = Path.Combine(_root, "Source", "FoxMouse");
        string copied = Path.Combine(_root, "Copied", "FoxMouse");
        CreateManagedRoot(source);
        CopyDirectory(source, copied);
        DeploymentPaths copiedPaths = _defaults.WithInstallRoot(copied);

        Assert.Equal(InstallationKind.Invalid, new DeploymentEngine().GetStatus(copiedPaths).Kind);
        Assert.Throws<InvalidDataException>(() =>
            InstallationLocator.ResolveForUninstall(_defaults, copied));
    }

    [Fact]
    public void CustomLocationLifecyclePreservesParentAndSiblingData()
    {
        string selectedParent = Path.Combine(_root, "SelectedParent");
        Directory.CreateDirectory(selectedParent);
        string parentSentinel = Path.Combine(selectedParent, "parent.txt");
        string sibling = Path.Combine(selectedParent, "SiblingProduct");
        File.WriteAllText(parentSentinel, "parent");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "sibling.txt"), "sibling");

        DeploymentPaths custom = InstallLocationPolicy.ResolvePathsForParent(selectedParent, _defaults);
        string package = CreatePackage();
        DeploymentEngine engine = new();
        engine.InstallOrRepair(new InstallRequest
        {
            Paths = custom,
            PackagePath = package,
            Version = "0.4.2",
            LaunchAfterInstall = false,
            ProcessController = new FakeProcessController(),
            ShortcutWriter = new FakeShortcutWriter(),
        });

        using JsonDocument state = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(custom.InstallRoot, ".foxmouse-install.json")));
        Assert.Equal(2, state.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal("FoxMouse.Desktop", state.RootElement.GetProperty("ProductId").GetString());
        Assert.Equal(custom.InstallRoot, state.RootElement.GetProperty("InstallRoot").GetString(), ignoreCase: true);
        Assert.True(Guid.TryParseExact(state.RootElement.GetProperty("InstallId").GetString(), "N", out _));
        Assert.Equal(custom.InstallRoot, InstallationLocator.ResolveForSetup(_defaults).InstallRoot, ignoreCase: true);
        IReadOnlyDictionary<string, object?> arp = ArpRegistration.ReadValues(_defaults);
        Assert.Equal(custom.InstallRoot, arp["InstallLocation"]);
        Assert.Contains(custom.InstallRoot, Assert.IsType<string>(arp["DisplayIcon"]), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_defaults.InstallRoot));

        engine.Uninstall(new UninstallRequest
        {
            Paths = InstallationLocator.ResolveForUninstall(_defaults),
            KeepSettings = true,
            ProcessController = new FakeProcessController(),
            ShortcutWriter = new FakeShortcutWriter(),
        });

        Assert.False(Directory.Exists(custom.InstallRoot));
        Assert.Equal("parent", File.ReadAllText(parentSentinel));
        Assert.Equal("sibling", File.ReadAllText(Path.Combine(sibling, "sibling.txt")));
    }

    [Fact]
    public void ExplicitDetachedRootCannotEscapeIsolationBoundary()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"Outside-{Guid.NewGuid():N}", "FoxMouse");

        Assert.Throws<InvalidDataException>(() =>
            InstallationLocator.ResolveForUninstall(_defaults, outside));
    }

    private void CreateManagedRoot(string installRoot)
    {
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "FoxMouse.exe"), "app");
        File.WriteAllText(Path.Combine(installRoot, "FoxMouse.Uninstall.exe"), "uninstall");
        File.WriteAllText(Path.Combine(installRoot, "FoxMouse.Cleanup.exe"), "cleanup");
        File.WriteAllText(
            Path.Combine(installRoot, ".foxmouse-install.json"),
            JsonSerializer.Serialize(new
            {
                Version = "0.4.2",
                InstalledUtc = DateTimeOffset.UtcNow,
                PackageSha256 = new string('a', 64),
                SchemaVersion = 2,
                ProductId = "FoxMouse.Desktop",
                InstallId = Guid.NewGuid().ToString("N"),
                InstallRoot = Path.GetFullPath(installRoot),
            }));
    }

    private string CreatePackage()
    {
        string package = Path.Combine(_root, $"package-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create);
        AddEntry(archive, "FoxMouse/FoxMouse.exe", "app");
        AddEntry(archive, "FoxMouse/FoxMouse.Guard.exe", "guard");
        AddEntry(archive, "FoxMouse/Settings/FoxMouse.Settings.exe", "settings");
        AddEntry(archive, "FoxMouse/FoxMouse.Uninstall.exe", "uninstaller");
        AddEntry(archive, "FoxMouse/FoxMouse.Cleanup.exe", "cleanup");
        return package;
    }

    private static void AddEntry(ZipArchive archive, string name, string contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open());
        writer.Write(contents);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeProcessController : IProductProcessController
    {
        public bool IsApplicationRunning(string installRoot) => false;

        public void StopSafely(string installRoot, string recoveryExecutable)
        {
            Assert.True(File.Exists(recoveryExecutable));
        }

        public void StartApplication(string installRoot) => throw new InvalidOperationException("Launch was disabled.");
    }

    private sealed class FakeShortcutWriter : IShortcutWriter
    {
        public void Create(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.WriteAllText(shortcutPath, targetPath);
        }
    }
}
