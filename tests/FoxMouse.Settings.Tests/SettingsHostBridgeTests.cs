using FoxMouse.App;
using FoxMouse.Platform.Windows.Configuration;
using FoxMouse.Platform.Windows.Diagnostics;

namespace FoxMouse.Settings.Tests;

public sealed class SettingsHostBridgeTests
{
    [Fact]
    public async Task MissingSettingsHostIsReportedImmediately()
    {
        using TemporaryDirectory temporary = new();
        SettingsStore store = new(temporary.Path);
        await using RollingFileLogger logger = new(Path.Combine(temporary.Path, "Logs"));
        await using SettingsHostBridge bridge = new(
            store,
            logger,
            new SynchronizationContext(),
            static () => { },
            static () => { },
            listenForChanges: false,
            settingsExecutableResolver: static () => null,
            launchReadyTimeout: TimeSpan.FromMilliseconds(100));

        Assert.False(await bridge.TryOpenAsync("general"));
    }

    [Fact]
    public async Task ProcessCreationWithoutWindowAcknowledgementIsFailure()
    {
        using TemporaryDirectory temporary = new();
        SettingsStore store = new(temporary.Path);
        await using RollingFileLogger logger = new(Path.Combine(temporary.Path, "Logs"));
        string harmlessExecutable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "where.exe");
        await using SettingsHostBridge bridge = new(
            store,
            logger,
            new SynchronizationContext(),
            static () => { },
            static () => { },
            listenForChanges: false,
            settingsExecutableResolver: () => harmlessExecutable,
            launchReadyTimeout: TimeSpan.FromMilliseconds(150));

        Assert.False(await bridge.TryOpenAsync("about"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"FoxMouse.SettingsHostBridge.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
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
