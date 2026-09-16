namespace FoxMouse.Settings;

public enum SettingsCloseChoice { Save, Discard, Cancel }

/// <summary>Shared decision rule for window close and whole-application exit.</summary>
public static class SettingsClosePolicy
{
    public static bool ShouldClose(SettingsCloseChoice choice, bool globalExit, bool saveSucceeded) =>
        choice switch
        {
            SettingsCloseChoice.Save => saveSucceeded,
            SettingsCloseChoice.Discard => true,
            SettingsCloseChoice.Cancel => globalExit,
            _ => false,
        };
}
