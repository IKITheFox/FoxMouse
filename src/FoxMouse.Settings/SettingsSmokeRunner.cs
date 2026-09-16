using FoxMouse.Core;
using FoxMouse.Platform.Windows.Configuration;
using FoxMouse.Settings.Services;

namespace FoxMouse.Settings;

internal static class SettingsSmokeRunner
{
    public static int Run()
    {
        string smokeRoot = Path.Combine(
            Path.GetTempPath(),
            "FoxMouse.Settings.Smoke",
            Guid.NewGuid().ToString("N"));

        try
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                return 10;
            }

            if (SettingsActivationRequest.Parse("--page exclusions").Page != SettingsPageKind.Exclusions ||
                SettingsActivationRequest.Parse("--page=about").Page != SettingsPageKind.About ||
                SettingsActivationRequest.Parse("--page unknown").Page != SettingsPageKind.General)
            {
                return 11;
            }

            SettingsStore store = new(smokeRoot);
            FoxMouseSettings expected = SettingsNormalizer.Normalize(FoxMouseSettings.Default with
            {
                Enabled = false,
                Sensitivity = 0.72,
                MaxScale = 4.2,
                ExcludedProcesses = ["smoke-game.exe"],
            });
            store.SaveAsync(expected).GetAwaiter().GetResult();
            FoxMouseSettings actual = store.LoadAsync().GetAwaiter().GetResult();
            if (!string.Equals(
                    SettingsJson.Serialize(actual),
                    SettingsJson.Serialize(expected),
                    StringComparison.Ordinal))
            {
                return 12;
            }

            SettingsChangeMessage message = SettingsChangeMessage.CreateReload(store.SettingsPath);
            if (message.ProtocolVersion != SettingsChangeMessage.CurrentProtocolVersion ||
                message.Command != SettingsChangeMessage.ReloadSettingsCommand ||
                message.SettingsPath != Path.GetFullPath(store.SettingsPath) ||
                message.Revision.Length != 32)
            {
                return 13;
            }

            SettingsChangeMessage previewMessage = SettingsChangeMessage.CreatePreview(store.SettingsPath);
            if (previewMessage.ProtocolVersion != SettingsChangeMessage.CurrentProtocolVersion ||
                previewMessage.Command != SettingsChangeMessage.PreviewCommand ||
                previewMessage.SettingsPath != Path.GetFullPath(store.SettingsPath) ||
                previewMessage.Revision.Length != 32)
            {
                return 15;
            }

            if (!ProcessExclusionName.TryNormalize(
                    @"C:\Program Files\Example\sample.exe",
                    out string executableName) ||
                executableName != "sample.exe")
            {
                return 14;
            }

            return 0;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            return 1;
        }
        finally
        {
            TryDeleteSmokeDirectory(smokeRoot);
        }
    }

    private static void TryDeleteSmokeDirectory(string smokeRoot)
    {
        try
        {
            string expectedParent = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "FoxMouse.Settings.Smoke"));
            string resolvedRoot = Path.GetFullPath(smokeRoot);
            if (resolvedRoot.StartsWith(
                    expectedParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
