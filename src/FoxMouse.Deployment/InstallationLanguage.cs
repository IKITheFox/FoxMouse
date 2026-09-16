using System.Text.Json;
using FoxMouse.Core;

namespace FoxMouse.Deployment;

public static class InstallationLanguage
{
    public static string ReadPreference(string settingsRoot)
    {
        string path = Path.Combine(settingsRoot, "settings.json");
        try
        {
            if (!File.Exists(path)) return "system";
            DeploymentFileSystem.EnsureNoReparsePoints(settingsRoot);
            using FileStream stream = File.OpenRead(path);
            if (stream.Length > 1024 * 1024) return "system";
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("language", out JsonElement language) &&
                language.ValueKind == JsonValueKind.String)
                return UiLanguage.Normalize(language.GetString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged or inaccessible preference must not block uninstall.
            // Do not repair/quarantine user configuration from this entry point.
        }
        return "system";
    }

    internal static string? Initialize(string settingsRoot, string? language)
    {
        if (language is null) return null;
        string path = Path.Combine(settingsRoot, "settings.json");
        Directory.CreateDirectory(settingsRoot);
        DeploymentFileSystem.EnsureNoReparsePoints(settingsRoot);
        if (File.Exists(path)) return null;
        string json = JsonSerializer.Serialize(new { language = UiLanguage.Normalize(language) });
        string temporary = Path.Combine(settingsRoot, $".language-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, json);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { return null; }
            return json;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Rollback(string settingsRoot, string? createdJson)
    {
        if (createdJson is null) return;
        DeploymentFileSystem.EnsureNoReparsePoints(settingsRoot);
        string path = Path.Combine(settingsRoot, "settings.json");
        // Preserve settings changed by the application after launch.
        if (File.Exists(path) && File.ReadAllText(path) == createdJson) File.Delete(path);
    }
}
