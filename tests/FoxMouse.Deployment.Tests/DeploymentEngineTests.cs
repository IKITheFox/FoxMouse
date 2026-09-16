using System.IO.Compression;
using System.Text.Json;
using FoxMouse.Deployment;
using Microsoft.Win32;

namespace FoxMouse.Deployment.Tests;

[Collection(DeploymentOperationCollection.Name)]
public sealed class DeploymentEngineTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void FreshInstallInitializesLanguageAndRepairPreservesIt(string language)
    {
        using TestDeployment test = new();
        string package = test.CreatePackage("app");
        test.Engine.InstallOrRepair(test.InstallRequest(package, initialLanguage: language));
        string path = Path.Combine(test.Paths.SettingsRoot, "settings.json");
        string before = File.ReadAllText(path);
        Assert.Equal(language, JsonDocument.Parse(before).RootElement.GetProperty("language").GetString());
        test.Engine.InstallOrRepair(test.InstallRequest(package, initialLanguage: "system"));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void FreshInstallDoesNotOverwriteRetainedSettings()
    {
        using TestDeployment test = new();
        Directory.CreateDirectory(test.Paths.SettingsRoot);
        string path = Path.Combine(test.Paths.SettingsRoot, "settings.json");
        File.WriteAllText(path, "{\"language\":\"zh-CN\",\"other\":42}");
        test.Engine.InstallOrRepair(test.InstallRequest(test.CreatePackage("app"), initialLanguage: "en-US"));
        Assert.Equal("{\"language\":\"zh-CN\",\"other\":42}", File.ReadAllText(path));
    }

    [Fact]
    public void FailedInstallRollsBackNewLanguageSettings()
    {
        using TestDeployment test = new();
        Assert.Throws<DeploymentException>(() => test.Engine.InstallOrRepair(test.InstallRequest(
            test.CreatePackage("app"), checkpoint: phase => { if (phase == "launched") throw new IOException("test failure"); },
            initialLanguage: "en-US")));
        Assert.False(File.Exists(Path.Combine(test.Paths.SettingsRoot, "settings.json")));
    }
    [Fact]
    public async Task AtomicFilePublisherNeverExposesPartialJsonDuringRepeatedOverwrite()
    {
        string root = Path.Combine(Path.GetTempPath(), $"FoxMouse-AtomicTests-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "result.json");
        Directory.CreateDirectory(root);
        AtomicFilePublisher.WriteAllText(path, JsonSerializer.Serialize(new { generation = 0, payload = new string('a', 4096) }));
        bool completed = false;
        Task reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref completed))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                    Assert.Equal(4096, document.RootElement.GetProperty("payload").GetString()!.Length);
                }
                catch (IOException)
                {
                    // A reader can briefly lose the replace race, but must never
                    // observe a published partial document.
                }
            }
        });

        try
        {
            for (int generation = 1; generation <= 100; generation++)
            {
                AtomicFilePublisher.WriteAllText(
                    path,
                    JsonSerializer.Serialize(new { generation, payload = new string('a', 4096) }));
            }
        }
        finally
        {
            Volatile.Write(ref completed, true);
            await reader;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConcurrentMaintenanceForTheSameInstallRootIsRejected()
    {
        string installRoot = Path.Combine(Path.GetTempPath(), $"FoxMouse-lock-{Guid.NewGuid():N}");
        using DeploymentOperationLock held = DeploymentOperationLock.Acquire(installRoot);

        Assert.Throws<InvalidOperationException>(() => DeploymentOperationLock.Acquire(installRoot));
    }

    [Fact]
    public void ConcurrentMaintenanceForDifferentInstallRootsIsAlsoRejected()
    {
        string firstRoot = Path.Combine(Path.GetTempPath(), $"FoxMouse-lock-a-{Guid.NewGuid():N}");
        string secondRoot = Path.Combine(Path.GetTempPath(), $"FoxMouse-lock-b-{Guid.NewGuid():N}");
        using DeploymentOperationLock held = DeploymentOperationLock.Acquire(firstRoot);

        Assert.Throws<InvalidOperationException>(() => DeploymentOperationLock.Acquire(secondRoot));
    }

    [Fact]
    public void ProcessSafetyRestoresAtMaintenanceBoundariesEvenWhenNothingIsRunning()
    {
        List<string> recoveryCalls = [];
        FoxMouseProductProcessController controller = new(path => recoveryCalls.Add(path));

        controller.StopSafely(Path.Combine(Path.GetTempPath(), "FoxMouse-not-running"), "recovery.exe");

        Assert.Equal(["recovery.exe", "recovery.exe"], recoveryCalls);
    }

    [Fact]
    public void ProcessSafetyStillAttemptsFinalRecoveryWhenInitialRecoveryFails()
    {
        int recoveryCalls = 0;
        InvalidOperationException expected = new("initial recovery failed");
        FoxMouseProductProcessController controller = new(_ =>
        {
            recoveryCalls++;
            if (recoveryCalls == 1)
            {
                throw expected;
            }
        });

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            controller.StopSafely(Path.Combine(Path.GetTempPath(), "FoxMouse-not-running"), "recovery.exe"));

        Assert.Same(expected, actual);
        Assert.Equal(2, recoveryCalls);
    }

    [Fact]
    public void ProcessSafetyReportsBothRecoveryFailures()
    {
        int recoveryCalls = 0;
        FoxMouseProductProcessController controller = new(_ =>
        {
            recoveryCalls++;
            throw new InvalidOperationException($"recovery {recoveryCalls} failed");
        });

        AggregateException error = Assert.Throws<AggregateException>(() =>
            controller.StopSafely(Path.Combine(Path.GetTempPath(), "FoxMouse-not-running"), "recovery.exe"));

        Assert.Equal(2, recoveryCalls);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains("recovery 1 failed", error.InnerExceptions[0].Message, StringComparison.Ordinal);
        Assert.Contains("recovery 2 failed", error.InnerExceptions[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanInstallCreatesPayloadCacheShortcutAndArpContract()
    {
        using TestDeployment test = new();
        string package = test.CreatePackage("new-app");

        DeploymentOutcome outcome = test.Engine.InstallOrRepair(test.InstallRequest(package));

        Assert.Equal(DeploymentOperation.Install, outcome.Operation);
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(test.Paths.InstallRoot, "FoxMouse.exe")));
        Assert.True(File.Exists(Path.Combine(test.Paths.InstallRoot, "FoxMouse.Uninstall.exe")));
        Assert.True(File.Exists(Path.Combine(test.Paths.InstallRoot, "FoxMouse.Cleanup.exe")));
        Assert.True(File.Exists(test.Paths.MaintenanceExecutable));
        Assert.True(File.Exists(test.Paths.MaintenanceCleanupExecutable));
        Assert.True(File.Exists(Path.Combine(test.Paths.InstallerCacheRoot, "FoxMouse-package.zip")));
        Assert.Equal(Path.Combine(test.Paths.InstallRoot, "FoxMouse.exe"), File.ReadAllText(test.Paths.StartMenuShortcut));

        IReadOnlyDictionary<string, object?> arp = ArpRegistration.ReadValues(test.Paths);
        Assert.Equal("FoxMouse", arp["DisplayName"]);
        Assert.Equal(TestDeployment.Version, arp["DisplayVersion"]);
        Assert.Equal(test.Paths.InstallRoot, arp["InstallLocation"]);
        Assert.Equal($"\"{Path.Combine(test.Paths.InstallRoot, "FoxMouse.Uninstall.exe")}\"", arp["UninstallString"]);
        Assert.Equal($"\"{test.Paths.MaintenanceExecutable}\" --uninstall --quiet", arp["QuietUninstallString"]);
        Assert.Equal($"\"{Path.Combine(test.Paths.InstallRoot, "FoxMouse.Uninstall.exe")}\"", arp["ModifyPath"]);
        Assert.Equal(0, arp["NoModify"]);
        Assert.Equal(0, arp["NoRepair"]);
        Assert.Equal(1, test.ProcessController.StopCalls);
    }

    [Fact]
    public void SameVersionRepairRestoresDeletedFileFromVerifiedCache()
    {
        using TestDeployment test = new();
        string package = test.CreatePackage("repair-app");
        test.Engine.InstallOrRepair(test.InstallRequest(package));
        string settingsExecutable = Path.Combine(test.Paths.InstallRoot, "Settings", "FoxMouse.Settings.exe");
        File.Delete(settingsExecutable);

        string cachedPackage = test.Engine.GetCachedPackagePath(test.Paths);
        DeploymentOutcome outcome = test.Engine.InstallOrRepair(test.InstallRequest(cachedPackage));

        Assert.Equal(DeploymentOperation.Repair, outcome.Operation);
        Assert.True(File.Exists(settingsExecutable));
        Assert.Equal(InstallationKind.Managed, test.Engine.GetStatus(test.Paths).Kind);
    }

    [Fact]
    public void LegacyInstallIsMigratedWithoutTouchingSettings()
    {
        using TestDeployment test = new();
        Directory.CreateDirectory(test.Paths.InstallRoot);
        File.WriteAllText(Path.Combine(test.Paths.InstallRoot, "FoxMouse.exe"), "legacy");
        File.WriteAllText(Path.Combine(test.Paths.InstallRoot, "legacy.marker"), "keep only on rollback");
        Directory.CreateDirectory(test.Paths.SettingsRoot);
        File.WriteAllText(Path.Combine(test.Paths.SettingsRoot, "settings.json"), "user-settings");
        test.ProcessController.Running = true;

        DeploymentOutcome outcome = test.Engine.InstallOrRepair(test.InstallRequest(test.CreatePackage("managed")));

        Assert.Equal(DeploymentOperation.Upgrade, outcome.Operation);
        Assert.Equal("managed", File.ReadAllText(Path.Combine(test.Paths.InstallRoot, "FoxMouse.exe")));
        Assert.False(File.Exists(Path.Combine(test.Paths.InstallRoot, "legacy.marker")));
        Assert.Equal("user-settings", File.ReadAllText(Path.Combine(test.Paths.SettingsRoot, "settings.json")));
        Assert.Equal(1, test.ProcessController.StopCalls);
    }

    [Fact]
    public void FailedUpgradeRestoresLegacyFilesArpShortcutAndRunningState()
    {
        using TestDeployment test = new();
        Directory.CreateDirectory(test.Paths.InstallRoot);
        File.WriteAllText(Path.Combine(test.Paths.InstallRoot, "FoxMouse.exe"), "legacy");
        File.WriteAllText(Path.Combine(test.Paths.InstallRoot, "legacy.marker"), "old");
        Directory.CreateDirectory(Path.GetDirectoryName(test.Paths.StartMenuShortcut)!);
        File.WriteAllText(test.Paths.StartMenuShortcut, "old-shortcut");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(test.Paths.UninstallRegistrySubKey, writable: true))
        {
            key.SetValue("DisplayName", "Legacy FoxMouse");
        }

        test.ProcessController.Running = true;
        InstallRequest request = test.InstallRequest(test.CreatePackage("new"), checkpoint: point =>
        {
            if (point == "registered")
            {
                throw new InvalidOperationException("injected registration failure");
            }
        });

        DeploymentException error = Assert.Throws<DeploymentException>(() => test.Engine.InstallOrRepair(request));

        Assert.Contains("injected registration failure", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(Path.Combine(test.Paths.InstallRoot, "legacy.marker")));
        Assert.Equal("old-shortcut", File.ReadAllText(test.Paths.StartMenuShortcut));
        Assert.Equal("Legacy FoxMouse", ArpRegistration.ReadValues(test.Paths)["DisplayName"]);
        Assert.False(File.Exists(test.Paths.MaintenanceExecutable));
        Assert.Equal(1, test.ProcessController.StartCalls);
    }

    [Fact]
    public void FailedRepairRestoresPreviousMaintenanceHost()
    {
        using TestDeployment test = new();
        string package = test.CreatePackage("app");
        test.Engine.InstallOrRepair(test.InstallRequest(package));
        File.WriteAllText(test.Paths.MaintenanceExecutable, "previous-maintenance-host");

        InstallRequest request = test.InstallRequest(package, checkpoint: point =>
        {
            if (point == "registered")
            {
                throw new InvalidOperationException("injected repair failure");
            }
        });

        _ = Assert.Throws<DeploymentException>(() => test.Engine.InstallOrRepair(request));

        Assert.Equal("previous-maintenance-host", File.ReadAllText(test.Paths.MaintenanceExecutable));
    }

    [Fact]
    public void UninstallFromManagedStateRemovesRegistrationAndCacheButKeepsSettingsByDefault()
    {
        using TestDeployment test = new();
        test.Engine.InstallOrRepair(test.InstallRequest(test.CreatePackage("app")));
        Directory.CreateDirectory(test.Paths.SettingsRoot);
        File.WriteAllText(Path.Combine(test.Paths.SettingsRoot, "settings.json"), "keep");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(test.Paths.StartupRegistrySubKey, writable: true))
        {
            key.SetValue("FoxMouse", "startup");
        }

        DeploymentOutcome outcome = test.Engine.Uninstall(new UninstallRequest
        {
            Paths = test.Paths,
            KeepSettings = true,
            ProcessController = test.ProcessController,
            ShortcutWriter = test.ShortcutWriter,
        });

        Assert.Equal(DeploymentOperation.Uninstall, outcome.Operation);
        Assert.False(Directory.Exists(test.Paths.InstallRoot));
        Assert.False(Directory.Exists(test.Paths.MaintenanceHostRoot));
        Assert.False(Directory.Exists(test.Paths.InstallerCacheRoot));
        Assert.True(File.Exists(Path.Combine(test.Paths.SettingsRoot, "settings.json")));
        Assert.False(File.Exists(test.Paths.StartMenuShortcut));
        Assert.Empty(ArpRegistration.ReadValues(test.Paths));
        using RegistryKey? resultKey = Registry.CurrentUser.OpenSubKey(test.Paths.StartupRegistrySubKey);
        Assert.DoesNotContain("FoxMouse", resultKey?.GetValueNames() ?? []);
    }

    [Fact]
    public void UninstallCanDeleteSettingsAfterExplicitChoice()
    {
        using TestDeployment test = new();
        test.Engine.InstallOrRepair(test.InstallRequest(test.CreatePackage("app")));
        Directory.CreateDirectory(test.Paths.SettingsRoot);
        File.WriteAllText(Path.Combine(test.Paths.SettingsRoot, "settings.json"), "delete");

        test.Engine.Uninstall(new UninstallRequest
        {
            Paths = test.Paths,
            KeepSettings = false,
            ProcessController = test.ProcessController,
            ShortcutWriter = test.ShortcutWriter,
        });

        Assert.False(Directory.Exists(test.Paths.SettingsRoot));
    }

    [Fact]
    public void FailedUninstallRestoresAllManagedState()
    {
        using TestDeployment test = new();
        test.Engine.InstallOrRepair(test.InstallRequest(test.CreatePackage("app")));
        Directory.CreateDirectory(test.Paths.SettingsRoot);
        File.WriteAllText(Path.Combine(test.Paths.SettingsRoot, "settings.json"), "settings");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(test.Paths.StartupRegistrySubKey, writable: true))
        {
            key.SetValue("FoxMouse", "startup");
        }

        test.ProcessController.Running = true;
        DeploymentException error = Assert.Throws<DeploymentException>(() => test.Engine.Uninstall(new UninstallRequest
        {
            Paths = test.Paths,
            KeepSettings = false,
            ProcessController = test.ProcessController,
            ShortcutWriter = test.ShortcutWriter,
            Checkpoint = point =>
            {
                if (point == "unregistered")
                {
                    throw new InvalidOperationException("injected uninstall failure");
                }
            },
        }));

        Assert.Contains("injected uninstall failure", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(test.Paths.InstallRoot, "FoxMouse.exe")));
        Assert.True(File.Exists(Path.Combine(test.Paths.InstallerCacheRoot, "FoxMouse-package.zip")));
        Assert.True(File.Exists(test.Paths.MaintenanceExecutable));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(test.Paths.SettingsRoot, "settings.json")));
        Assert.True(File.Exists(test.Paths.StartMenuShortcut));
        Assert.Equal("FoxMouse", ArpRegistration.ReadValues(test.Paths)["DisplayName"]);
        using RegistryKey? resultKey = Registry.CurrentUser.OpenSubKey(test.Paths.StartupRegistrySubKey);
        Assert.Equal("startup", resultKey?.GetValue("FoxMouse"));
        Assert.Equal(1, test.ProcessController.StartCalls);
    }

    [Fact]
    public void RepairRejectsTamperedCache()
    {
        using TestDeployment test = new();
        test.Engine.InstallOrRepair(test.InstallRequest(test.CreatePackage("app")));
        string cachedPackage = Path.Combine(test.Paths.InstallerCacheRoot, "FoxMouse-package.zip");
        using (FileStream stream = new(cachedPackage, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(42);
        }

        Assert.Throws<InvalidDataException>(() => test.Engine.GetCachedPackagePath(test.Paths));
    }

    [Fact]
    public void RepairRejectsCacheWhoseHashFileWasReboundButInstallStateWasNot()
    {
        using TestDeployment test = new();
        test.Engine.InstallOrRepair(test.InstallRequest(test.CreatePackage("app")));
        string cachedPackage = Path.Combine(test.Paths.InstallerCacheRoot, "FoxMouse-package.zip");
        using (FileStream stream = new(cachedPackage, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(42);
        }

        File.WriteAllText(
            Path.Combine(test.Paths.InstallerCacheRoot, "FoxMouse-package.sha256"),
            DeploymentFileSystem.ComputeSha256(cachedPackage));

        Assert.Throws<InvalidDataException>(() => test.Engine.GetCachedPackagePath(test.Paths));
    }

    [Fact]
    public void InstallRejectsZipTraversalAndLeavesNoRegistration()
    {
        using TestDeployment test = new();
        string package = test.CreateTraversalPackage();

        Assert.Throws<DeploymentException>(() => test.Engine.InstallOrRepair(test.InstallRequest(package)));
        Assert.False(Directory.Exists(test.Paths.InstallRoot));
        Assert.Empty(ArpRegistration.ReadValues(test.Paths));
    }

    [Fact]
    public void InstallRejectsAlternateDataStreamZipEntry()
    {
        using TestDeployment test = new();
        string package = test.CreatePackageWithExtraEntry("FoxMouse/Settings/payload:stream", "bad");

        DeploymentException error = Assert.Throws<DeploymentException>(() =>
            test.Engine.InstallOrRepair(test.InstallRequest(package)));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.False(Directory.Exists(test.Paths.InstallRoot));
    }

    [Fact]
    public void InstallRejectsFileDirectoryTargetConflict()
    {
        using TestDeployment test = new();
        string package = test.CreatePackageWithExtraEntry("FoxMouse/Settings", "conflict");

        DeploymentException error = Assert.Throws<DeploymentException>(() =>
            test.Engine.InstallOrRepair(test.InstallRequest(package)));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.False(Directory.Exists(test.Paths.InstallRoot));
    }

    [Fact]
    public void InstallRejectsArchiveEntryCountAboveLimit()
    {
        using TestDeployment test = new();
        string package = test.CreateOversizedEntryCountPackage();

        DeploymentException error = Assert.Throws<DeploymentException>(() =>
            test.Engine.InstallOrRepair(test.InstallRequest(package)));

        InvalidDataException invalidData = Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Contains("too many entries", invalidData.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(test.Paths.InstallRoot));
    }

    private sealed class TestDeployment : IDisposable
    {
        internal const string Version = "0.4.1";
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"FoxMouse-InstallerTests-{Guid.NewGuid():N}");
        private readonly string _registryRoot = $@"Software\FoxMouse\Tests\Installer-{Guid.NewGuid():N}";

        internal TestDeployment()
        {
            Paths = new DeploymentPaths(
                Path.Combine(_root, "Programs", "FoxMouse"),
                Path.Combine(_root, "LocalAppData", "FoxMouse"),
                Path.Combine(_root, "LocalAppData", "FoxMouse", "InstallerCache"),
                Path.Combine(_root, "StartMenu", "FoxMouse.lnk"),
                _registryRoot + @"\Uninstall\FoxMouse",
                _registryRoot + @"\Run")
            {
                IsolationRoot = _root,
            };
        }

        internal DeploymentEngine Engine { get; } = new();

        internal DeploymentPaths Paths { get; }

        internal FakeProcessController ProcessController { get; } = new();

        internal FakeShortcutWriter ShortcutWriter { get; } = new();

        internal InstallRequest InstallRequest(string package, Action<string>? checkpoint = null, string? initialLanguage = null) => new()
        {
            Paths = Paths,
            PackagePath = package,
            Version = Version,
            LaunchAfterInstall = false,
            ProcessController = ProcessController,
            ShortcutWriter = ShortcutWriter,
            Checkpoint = checkpoint,
            InitialLanguage = initialLanguage,
        };

        internal string CreatePackage(string appContents)
        {
            Directory.CreateDirectory(_root);
            string package = Path.Combine(_root, $"package-{Guid.NewGuid():N}.zip");
            using ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create);
            AddEntry(archive, "FoxMouse/FoxMouse.exe", appContents);
            AddEntry(archive, "FoxMouse/FoxMouse.Guard.exe", "guard");
            AddEntry(archive, "FoxMouse/Settings/FoxMouse.Settings.exe", "settings");
            AddEntry(archive, "FoxMouse/FoxMouse.Uninstall.exe", "uninstaller");
            AddEntry(archive, "FoxMouse/FoxMouse.Cleanup.exe", "cleanup");
            return package;
        }

        internal string CreateTraversalPackage()
        {
            Directory.CreateDirectory(_root);
            string package = Path.Combine(_root, "traversal.zip");
            using ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create);
            AddEntry(archive, "FoxMouse/../outside.txt", "bad");
            return package;
        }

        internal string CreatePackageWithExtraEntry(string entryName, string contents)
        {
            Directory.CreateDirectory(_root);
            string package = Path.Combine(_root, $"package-extra-{Guid.NewGuid():N}.zip");
            using ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create);
            AddEntry(archive, "FoxMouse/FoxMouse.exe", "app");
            AddEntry(archive, "FoxMouse/FoxMouse.Guard.exe", "guard");
            AddEntry(archive, "FoxMouse/Settings/FoxMouse.Settings.exe", "settings");
            AddEntry(archive, "FoxMouse/FoxMouse.Uninstall.exe", "uninstaller");
            AddEntry(archive, "FoxMouse/FoxMouse.Cleanup.exe", "cleanup");
            AddEntry(archive, entryName, contents);
            return package;
        }

        internal string CreateOversizedEntryCountPackage()
        {
            Directory.CreateDirectory(_root);
            string package = Path.Combine(_root, "too-many-entries.zip");
            using ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create);
            for (int index = 0; index <= DeploymentFileSystem.MaxArchiveEntries; index++)
            {
                _ = archive.CreateEntry($"FoxMouse/empty-{index}.txt");
            }

            return package;
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void AddEntry(ZipArchive archive, string name, string contents)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using StreamWriter writer = new(entry.Open());
            writer.Write(contents);
        }
    }

    private sealed class FakeProcessController : IProductProcessController
    {
        internal bool Running { get; set; }

        internal int StopCalls { get; private set; }

        internal int StartCalls { get; private set; }

        public bool IsApplicationRunning(string installRoot) => Running;

        public void StopSafely(string installRoot, string recoveryExecutable)
        {
            Assert.True(File.Exists(recoveryExecutable));
            StopCalls++;
            Running = false;
        }

        public void StartApplication(string installRoot)
        {
            Assert.True(File.Exists(Path.Combine(installRoot, "FoxMouse.exe")));
            StartCalls++;
            Running = true;
        }
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
