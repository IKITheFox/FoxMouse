using System.Runtime.InteropServices;

namespace FoxMouse.App.UI;

internal static class DwmWindowTheme
{
    private const uint UseImmersiveDarkModeBefore20H1 = 19;
    private const uint UseImmersiveDarkMode = 20;
    private const uint WindowCornerPreference = 33;
    private const int RoundCorner = 2;

    internal static void TryApply(nint windowHandle, SemanticColors colors)
    {
        if (windowHandle == nint.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        int dark = colors.IsDark && !colors.IsHighContrast ? 1 : 0;
        try
        {
            int result = DwmSetWindowAttribute(
                windowHandle,
                UseImmersiveDarkMode,
                ref dark,
                sizeof(int));
            if (result != 0)
            {
                _ = DwmSetWindowAttribute(
                    windowHandle,
                    UseImmersiveDarkModeBefore20H1,
                    ref dark,
                    sizeof(int));
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int corner = RoundCorner;
                _ = DwmSetWindowAttribute(
                    windowHandle,
                    WindowCornerPreference,
                    ref corner,
                    sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (SEHException)
        {
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        uint attribute,
        ref int attributeValue,
        int attributeSize);
}
