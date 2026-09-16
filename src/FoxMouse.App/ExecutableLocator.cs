namespace FoxMouse.App;

internal static class ExecutableLocator
{
    internal static string Current
    {
        get
        {
            string appHost = Path.Combine(AppContext.BaseDirectory, "FoxMouse.exe");
            if (File.Exists(appHost))
            {
                return appHost;
            }

            return Environment.ProcessPath ?? Application.ExecutablePath;
        }
    }
}
