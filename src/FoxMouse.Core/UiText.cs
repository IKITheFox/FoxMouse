using System.Globalization;
using System.Resources;

namespace FoxMouse.Core;

public static class UiText
{
    private static readonly ResourceManager Resources = new("FoxMouse.Core.Strings", typeof(UiText).Assembly);
    public static string Language { get; private set; } = UiLanguage.Resolve("system");
    public static void Configure(string? preference) => Language = UiLanguage.Resolve(preference);
    public static string Get(string key) => Get(key, Language);
    public static string Get(string key, string language) =>
        Resources.GetString(key, CultureInfo.GetCultureInfo(UiLanguage.Resolve(language)))
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");
    public static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    public static string SettingsSaveFailure(Exception exception, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // OS exception messages follow the Windows language, not our selected
        // UI language, and can contain private paths. Keep a diagnostic code.
        return string.Format(CultureInfo.InvariantCulture, Get("SettingsSaveFailure", language ?? Language),
            unchecked((uint)exception.HResult).ToString("X8", CultureInfo.InvariantCulture));
    }
}
