using Microsoft.Win32;

namespace FoxMouse.Deployment;

internal sealed record RegistryValueState(string Name, object Value, RegistryValueKind Kind);

internal sealed class RegistryKeyState
{
    private RegistryKeyState(bool existed, IReadOnlyList<RegistryValueState> values)
    {
        Existed = existed;
        Values = values;
    }

    internal bool Existed { get; }

    internal IReadOnlyList<RegistryValueState> Values { get; }

    internal static RegistryKeyState Capture(string subKey)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            return new RegistryKeyState(false, []);
        }

        List<RegistryValueState> values = [];
        foreach (string name in key.GetValueNames())
        {
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is not null)
            {
                values.Add(new RegistryValueState(name, value, key.GetValueKind(name)));
            }
        }

        return new RegistryKeyState(true, values);
    }

    internal void Restore(string subKey)
    {
        Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        if (!Existed)
        {
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        foreach (RegistryValueState value in Values)
        {
            key.SetValue(value.Name, value.Value, value.Kind);
        }
    }
}

internal sealed record RegistryValueSnapshot(bool Existed, object? Value, RegistryValueKind Kind)
{
    internal static RegistryValueSnapshot Capture(string subKey, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.Ordinal))
        {
            return new RegistryValueSnapshot(false, null, RegistryValueKind.None);
        }

        return new RegistryValueSnapshot(
            true,
            key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            key.GetValueKind(name));
    }

    internal void Restore(string subKey, string name)
    {
        if (!Existed)
        {
            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name, Value!, Kind);
    }
}

public static class ArpRegistration
{
    public static IReadOnlyDictionary<string, object?> ReadValues(DeploymentPaths paths)
    {
        DeploymentPaths normalized = paths.NormalizeAndValidate();
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(normalized.UninstallRegistrySubKey, writable: false);
        if (key is null)
        {
            return new Dictionary<string, object?>();
        }

        return key.GetValueNames().ToDictionary(
            name => name,
            name => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            StringComparer.Ordinal);
    }

    internal static void Write(DeploymentPaths paths, string version, string publisher, string? aboutUrl)
    {
        string app = Path.Combine(paths.InstallRoot, "FoxMouse.exe");
        string uninstaller = Path.Combine(paths.InstallRoot, "FoxMouse.Uninstall.exe");
        int estimatedKilobytes = checked((int)Math.Min(int.MaxValue, (DeploymentFileSystem.DirectorySize(paths.InstallRoot) + 1023L) / 1024L));

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(paths.UninstallRegistrySubKey, writable: true);
        key.SetValue("DisplayName", "FoxMouse", RegistryValueKind.String);
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        key.SetValue("Publisher", publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", paths.InstallRoot, RegistryValueKind.String);
        key.SetValue("DisplayIcon", Quote(app) + ",0", RegistryValueKind.String);
        key.SetValue("UninstallString", Quote(uninstaller), RegistryValueKind.String);
        key.SetValue("QuietUninstallString", Quote(paths.MaintenanceExecutable) + " --uninstall --quiet", RegistryValueKind.String);
        key.SetValue("ModifyPath", Quote(uninstaller), RegistryValueKind.String);
        key.SetValue("NoModify", 0, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", estimatedKilobytes, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
        if (!string.IsNullOrWhiteSpace(aboutUrl))
        {
            key.SetValue("URLInfoAbout", aboutUrl, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("URLInfoAbout", throwOnMissingValue: false);
        }
    }

    internal static void Remove(DeploymentPaths paths)
    {
        Registry.CurrentUser.DeleteSubKeyTree(paths.UninstallRegistrySubKey, throwOnMissingSubKey: false);
    }

    private static string Quote(string value) => $"\"{value}\"";
}
