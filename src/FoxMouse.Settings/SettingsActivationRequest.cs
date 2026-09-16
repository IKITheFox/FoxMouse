using Microsoft.Windows.AppLifecycle;
using FoxMouse.Platform.Windows.Configuration;
using Windows.ApplicationModel.Activation;

namespace FoxMouse.Settings;

public enum SettingsPageKind
{
    General,
    Exclusions,
    About,
}

public sealed record SettingsActivationRequest(SettingsPageKind Page, string? LaunchToken = null)
{
    public static SettingsActivationRequest Default { get; } = new(SettingsPageKind.General);

    public static SettingsActivationRequest Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Default;
        }

        string[] tokens = arguments.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SettingsPageKind selectedPage = SettingsPageKind.General;
        string? launchToken = null;
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            string? value = null;
            if (token.Equals("--page", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Length)
            {
                value = tokens[++index];
            }
            else if (token.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                value = token["--page=".Length..];
            }

            if (TryParsePage(value, out SettingsPageKind page))
            {
                selectedPage = page;
                continue;
            }

            value = null;
            if (token.Equals(SettingsHostReadyChannel.ArgumentName, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < tokens.Length)
            {
                value = tokens[++index];
            }
            else if (token.StartsWith(SettingsHostReadyChannel.ArgumentName + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = token[(SettingsHostReadyChannel.ArgumentName.Length + 1)..];
            }

            if (SettingsHostReadyChannel.IsValidToken(value))
            {
                launchToken = value;
            }
        }

        return new SettingsActivationRequest(selectedPage, launchToken);
    }

    internal static SettingsActivationRequest FromActivationArguments(AppActivationArguments arguments)
    {
        if (arguments.Kind == ExtendedActivationKind.Launch &&
            arguments.Data is ILaunchActivatedEventArgs launchArguments)
        {
            return Parse(launchArguments.Arguments);
        }

        return Default;
    }

    private static bool TryParsePage(string? value, out SettingsPageKind page)
    {
        page = value?.ToLowerInvariant() switch
        {
            "general" or "settings" => SettingsPageKind.General,
            "exclusions" or "excluded" or "processes" => SettingsPageKind.Exclusions,
            "about" => SettingsPageKind.About,
            _ => (SettingsPageKind)(-1),
        };
        return Enum.IsDefined(page);
    }
}
