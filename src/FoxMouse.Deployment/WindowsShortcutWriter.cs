using System.Runtime.InteropServices;

namespace FoxMouse.Deployment;

public sealed class WindowsShortcutWriter : IShortcutWriter
{
    public void Create(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        object shell = Activator.CreateInstance(shellType)!;
        object? shortcut = null;
        try
        {
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = targetPath;
            dynamicShortcut.WorkingDirectory = workingDirectory;
            dynamicShortcut.Description = description;
            dynamicShortcut.IconLocation = targetPath + ",0";
            dynamicShortcut.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                _ = Marshal.FinalReleaseComObject(shortcut);
            }

            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
