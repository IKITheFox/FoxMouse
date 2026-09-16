using System.Reflection;

namespace FoxMouse.Core;

public static class ProductRelease
{
    public static string DisplayVersion => FormatInformationalVersion(
        typeof(ProductRelease).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ProductRelease).Assembly.GetName().Version?.ToString(3) ?? "Unknown");

    public static string FormatInformationalVersion(string version)
    {
        string value = version.Split('+')[0];
        return value.EndsWith("-beta", StringComparison.OrdinalIgnoreCase)
            ? value[..^5] + " Beta" : value;
    }

    // Keep persisted versions numeric for compatibility with existing installation records.
    public static string DisplayInstalledVersion(string version) =>
        version == typeof(ProductRelease).Assembly.GetName().Version?.ToString(3)
            ? DisplayVersion : version;
}
