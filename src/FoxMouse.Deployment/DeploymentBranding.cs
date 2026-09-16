using Microsoft.Win32;

namespace FoxMouse.Deployment;

public enum DeploymentBrandVariant
{
    Light,
    Dark,
    HighContrastBlack,
    HighContrastWhite,
}

public static class DeploymentBranding
{
    private const string ResourcePrefix = "FoxMouse.Deployment.Branding.";
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static DeploymentBrandVariant DetectCurrentVariant() =>
        ResolveVariant(
            SystemInformation.HighContrast,
            AppsUseLightTheme(),
            SystemColors.WindowText);

    public static DeploymentBrandVariant ResolveVariant(
        bool highContrast,
        bool appsUseLightTheme,
        Color foreground)
    {
        if (highContrast)
        {
            double luminance = ((0.2126 * foreground.R) +
                                (0.7152 * foreground.G) +
                                (0.0722 * foreground.B)) / 255d;
            return luminance >= 0.5
                ? DeploymentBrandVariant.HighContrastWhite
                : DeploymentBrandVariant.HighContrastBlack;
        }

        return appsUseLightTheme ? DeploymentBrandVariant.Light : DeploymentBrandVariant.Dark;
    }

    public static Icon CreateIcon(DeploymentBrandVariant variant)
    {
        using Stream stream = OpenResource(GetIconResourceName(variant));
        using Icon source = new(stream);
        return (Icon)source.Clone();
    }

    public static Bitmap CreateMark(DeploymentBrandVariant variant)
    {
        using Stream stream = OpenResource(GetMarkResourceName(variant));
        using Bitmap source = new(stream);
        return new Bitmap(source);
    }

    public static bool AppsUseLightTheme()
    {
        try
        {
            using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            return personalize?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static Stream OpenResource(string name) =>
        typeof(DeploymentBranding).Assembly.GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidOperationException($"Missing embedded branding resource: {name}");

    private static string GetIconResourceName(DeploymentBrandVariant variant) => variant switch
    {
        DeploymentBrandVariant.Dark => "FoxMouse.Dark.ico",
        DeploymentBrandVariant.HighContrastBlack => "FoxMouse.HighContrast.Black.ico",
        DeploymentBrandVariant.HighContrastWhite => "FoxMouse.HighContrast.White.ico",
        _ => "FoxMouse.ico",
    };

    private static string GetMarkResourceName(DeploymentBrandVariant variant) => variant switch
    {
        DeploymentBrandVariant.Dark => "FoxMouse.Mark.Dark.png",
        DeploymentBrandVariant.HighContrastBlack => "FoxMouse.Mark.HighContrast.Black.png",
        DeploymentBrandVariant.HighContrastWhite => "FoxMouse.Mark.HighContrast.White.png",
        _ => "FoxMouse.Mark.Light.png",
    };
}
