using System.Text;

namespace FoxMouse.Settings;

internal static class SettingsStartupDiagnostics
{
    public static void Write(Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FoxMouse",
                "Logs");
            Directory.CreateDirectory(directory);
            // Messages and stack traces can contain user names, installation
            // paths and command-line arguments. Keep startup diagnostics useful
            // without persisting any of that local data.
            string entry = $"{DateTimeOffset.UtcNow:O}\tERROR\tsettings-startup-failed\t{exception.GetType().Name}; hresult=0x{exception.HResult:X8}{Environment.NewLine}";
            File.AppendAllText(
                Path.Combine(directory, "FoxMouse.Settings.log"),
                entry,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException)
        {
        }
    }
}
