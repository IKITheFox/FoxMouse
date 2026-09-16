namespace FoxMouse.Platform.Windows.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WindowsDesktopFactAttribute : FactAttribute
{
    public WindowsDesktopFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "This smoke test requires Windows.";
        }
        else if (!Environment.UserInteractive)
        {
            Skip = "This smoke test requires an interactive Windows desktop.";
        }
    }
}
