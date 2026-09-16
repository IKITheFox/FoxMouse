using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxMouse.Core;

public enum CursorEffectMode
{
    HighFidelity,
    Compatibility,
}

public sealed record FoxMouseSettings
{
    public const int CurrentSchemaVersion = 1;

    public static FoxMouseSettings Default { get; } = new();

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("language")]
    public string Language { get; init; } = "system";

    [JsonPropertyName("mode")]
    public CursorEffectMode Mode { get; init; } = CursorEffectMode.HighFidelity;

    [JsonPropertyName("sensitivity")]
    public double Sensitivity { get; init; } = 0.55;

    [JsonPropertyName("maxScale")]
    public double MaxScale { get; init; } = 3.5;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; init; }

    [JsonPropertyName("disableWhileDragging")]
    public bool DisableWhileDragging { get; init; } = true;

    [JsonPropertyName("pauseInFullscreen")]
    public bool PauseInFullscreen { get; init; } = true;

    [JsonPropertyName("excludedProcesses")]
    public string[] ExcludedProcesses { get; init; } = [];
}

public static class SettingsNormalizer
{
    public const double MinimumScale = 1.5;
    public const double MaximumScale = 6.0;

    public static FoxMouseSettings Normalize(FoxMouseSettings? settings)
    {
        settings ??= FoxMouseSettings.Default;
        var mode = Enum.IsDefined(settings.Mode)
            ? settings.Mode
            : CursorEffectMode.HighFidelity;

        return settings with
        {
            SchemaVersion = FoxMouseSettings.CurrentSchemaVersion,
            Mode = mode,
            Language = UiLanguage.Normalize(settings.Language),
            Sensitivity = FiniteClamp(settings.Sensitivity, 0.0, 1.0, FoxMouseSettings.Default.Sensitivity),
            MaxScale = FiniteClamp(settings.MaxScale, MinimumScale, MaximumScale, FoxMouseSettings.Default.MaxScale),
            ExcludedProcesses = NormalizeExcludedProcesses(settings.ExcludedProcesses),
        };
    }

    private static string[] NormalizeExcludedProcesses(IEnumerable<string>? processNames)
    {
        if (processNames is null)
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var processName in processNames)
        {
            var normalized = processName?.Trim();
            if (string.IsNullOrEmpty(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            result.Add(normalized);
            if (result.Count == 128)
            {
                break;
            }
        }

        return [.. result];
    }

    private static double FiniteClamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public static class SettingsJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(FoxMouseSettings settings) =>
        JsonSerializer.Serialize(SettingsNormalizer.Normalize(settings), Options);

    public static FoxMouseSettings Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var settings = JsonSerializer.Deserialize<FoxMouseSettings>(json, Options);
        return SettingsNormalizer.Normalize(settings);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
