using System.Globalization;

namespace FoxMouse.Core;

/// <summary>Shared language policy; display language does not change number formatting.</summary>
public static class UiLanguage
{
    public static string Normalize(string? language) => language?.ToLowerInvariant() switch
    {
        "zh-cn" => "zh-CN",
        "en-us" => "en-US",
        _ => "system",
    };

    public static string Resolve(string? preference, CultureInfo? systemCulture = null)
    {
        string language = Normalize(preference);
        if (language != "system") return language;
        return (systemCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "zh"
            ? "zh-CN" : "en-US";
    }
}
