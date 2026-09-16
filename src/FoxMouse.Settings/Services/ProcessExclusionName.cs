namespace FoxMouse.Settings.Services;

public static class ProcessExclusionName
{
    public static bool TryNormalize(string? candidate, out string executableName)
    {
        executableName = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string trimmed = candidate.Trim().Trim('"');
        string fileName;
        try
        {
            fileName = Path.GetFileName(trimmed);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Length > 260 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        executableName = fileName;
        return true;
    }
}
