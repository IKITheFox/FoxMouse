using System.Text;
using FoxMouse.Core;
using FoxMouse.Platform.Windows.Configuration;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task LoadMissingSettingsReturnsDefaultsWithoutCreatingStorage()
    {
        using TemporaryDirectory temporary = new();
        string root = System.IO.Path.Combine(temporary.Path, "not-created-yet");
        SettingsStore store = new(root);

        FoxMouseSettings loaded = await store.LoadAsync();

        Assert.Equal(FoxMouseSettings.Default, loaded);
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(store.SettingsPath));
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsNormalizedSettings()
    {
        using TemporaryDirectory temporary = new();
        SettingsStore store = new(temporary.Path);
        FoxMouseSettings input = new()
        {
            Enabled = false,
            Mode = CursorEffectMode.Compatibility,
            Sensitivity = 0.81,
            MaxScale = 4.75,
            StartWithWindows = false,
            DisableWhileDragging = false,
            PauseInFullscreen = false,
            ExcludedProcesses = [" explorer.exe ", "EXPLORER.EXE", "game.exe"],
        };

        await store.SaveAsync(input);
        FoxMouseSettings loaded = await store.LoadAsync();

        Assert.False(loaded.Enabled);
        Assert.Equal(CursorEffectMode.Compatibility, loaded.Mode);
        Assert.Equal(0.81, loaded.Sensitivity);
        Assert.Equal(4.75, loaded.MaxScale);
        Assert.False(loaded.StartWithWindows);
        Assert.False(loaded.DisableWhileDragging);
        Assert.False(loaded.PauseInFullscreen);
        Assert.Equal(["explorer.exe", "game.exe"], loaded.ExcludedProcesses);
        Assert.Contains("\"schemaVersion\"", await File.ReadAllTextAsync(store.SettingsPath, Encoding.UTF8));
    }

    [Fact]
    public async Task CorruptSettingsAreQuarantinedAndDefaultsAreReturned()
    {
        using TemporaryDirectory temporary = new();
        SettingsStore store = new(temporary.Path);
        const string corruptJson = "{ \"enabled\": tru definitely-not-json";
        await File.WriteAllTextAsync(store.SettingsPath, corruptJson, Encoding.UTF8);

        FoxMouseSettings loaded = await store.LoadAsync();

        Assert.Equal(FoxMouseSettings.Default, loaded);
        Assert.False(File.Exists(store.SettingsPath));
        string brokenPath = Assert.Single(Directory.GetFiles(temporary.Path, "settings.json.broken-*"));
        Assert.Equal(corruptJson, await File.ReadAllTextAsync(brokenPath, Encoding.UTF8));
    }

    [Fact]
    public async Task ReplacingExistingSettingsKeepsPreviousBackupAndNoTemporaryFile()
    {
        using TemporaryDirectory temporary = new();
        SettingsStore store = new(temporary.Path);
        FoxMouseSettings previous = new()
        {
            Sensitivity = 0.2,
            MaxScale = 2.25,
            ExcludedProcesses = ["previous.exe"],
        };
        FoxMouseSettings replacement = new()
        {
            Sensitivity = 0.9,
            MaxScale = 5.5,
            ExcludedProcesses = ["replacement.exe"],
        };

        await store.SaveAsync(previous);
        string previousJson = await File.ReadAllTextAsync(store.SettingsPath, Encoding.UTF8);
        await store.SaveAsync(replacement);

        FoxMouseSettings loaded = await store.LoadAsync();
        Assert.Equal(0.9, loaded.Sensitivity);
        Assert.Equal(5.5, loaded.MaxScale);
        Assert.Equal(["replacement.exe"], loaded.ExcludedProcesses);
        Assert.Equal(
            previousJson,
            await File.ReadAllTextAsync($"{store.SettingsPath}.bak", Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSavesAreSerializedAndLatestRequestWins()
    {
        using TemporaryDirectory temporary = new();
        SettingsStore store = new(temporary.Path);
        string largeValue = new('x', 64 * 1024);
        FoxMouseSettings slowOlderRequest = new()
        {
            Sensitivity = 0.1,
            ExcludedProcesses = Enumerable.Range(0, 128)
                .Select(index => $"older-{index:D3}-{largeValue}")
                .ToArray(),
        };
        FoxMouseSettings latestRequest = new()
        {
            Sensitivity = 0.95,
            MaxScale = 5.75,
            ExcludedProcesses = ["latest-request.exe"],
        };

        Task olderSave = store.SaveAsync(slowOlderRequest);
        Task latestSave = store.SaveAsync(latestRequest);
        await Task.WhenAll(olderSave, latestSave);

        FoxMouseSettings loaded = await store.LoadAsync();
        Assert.Equal(0.95, loaded.Sensitivity);
        Assert.Equal(5.75, loaded.MaxScale);
        Assert.Equal(["latest-request.exe"], loaded.ExcludedProcesses);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }
}
