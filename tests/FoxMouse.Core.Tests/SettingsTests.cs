using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void DefaultsMatchV01Contract()
    {
        var settings = FoxMouseSettings.Default;

        Assert.True(settings.Enabled);
        Assert.Equal(CursorEffectMode.HighFidelity, settings.Mode);
        Assert.Equal(0.55, settings.Sensitivity);
        Assert.Equal(3.5, settings.MaxScale);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.DisableWhileDragging);
        Assert.True(settings.PauseInFullscreen);
        Assert.Empty(settings.ExcludedProcesses);
    }

    [Fact]
    public void NormalizeClampsNumbersAndRepairsEnumAndSchema()
    {
        var settings = new FoxMouseSettings
        {
            SchemaVersion = -10,
            Mode = (CursorEffectMode)999,
            Sensitivity = double.PositiveInfinity,
            MaxScale = 99,
        };

        var normalized = SettingsNormalizer.Normalize(settings);

        Assert.Equal(FoxMouseSettings.CurrentSchemaVersion, normalized.SchemaVersion);
        Assert.Equal(CursorEffectMode.HighFidelity, normalized.Mode);
        Assert.Equal(FoxMouseSettings.Default.Sensitivity, normalized.Sensitivity);
        Assert.Equal(SettingsNormalizer.MaximumScale, normalized.MaxScale);
    }

    [Fact]
    public void NormalizeTrimsDeduplicatesAndLimitsExcludedProcesses()
    {
        var names = Enumerable.Range(0, 140)
            .Select(static index => $" app-{index}.exe ")
            .Prepend("   ")
            .Prepend("APP-0.EXE")
            .ToArray();

        var normalized = SettingsNormalizer.Normalize(new FoxMouseSettings
        {
            ExcludedProcesses = names,
        });

        Assert.Equal(128, normalized.ExcludedProcesses.Length);
        Assert.Equal("APP-0.EXE", normalized.ExcludedProcesses[0]);
        Assert.DoesNotContain(normalized.ExcludedProcesses, string.IsNullOrWhiteSpace);
        Assert.Equal(
            normalized.ExcludedProcesses.Length,
            normalized.ExcludedProcesses.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SettingsJsonUsesStableCamelCaseContractAndNormalizes()
    {
        var json = SettingsJson.Serialize(new FoxMouseSettings
        {
            Mode = CursorEffectMode.Compatibility,
            ExcludedProcesses = [" game.exe ", "GAME.EXE"],
        });
        var roundTrip = SettingsJson.Deserialize(json);

        Assert.Contains("\"pauseInFullscreen\"", json, StringComparison.Ordinal);
        Assert.Contains("\"compatibility\"", json, StringComparison.Ordinal);
        Assert.Equal(["game.exe"], roundTrip.ExcludedProcesses);
    }
}
