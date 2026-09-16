using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FoxMouse.App.UI;

namespace FoxMouse.App;

internal static class TrayMenuVisualSmokeRunner
{
    internal static int Run(string? outputPath)
    {
        try
        {
            WindowsThemeService themeService = new();
            using ModernTrayMenuForm menu = new(themeService);
            menu.UpdateState(enabled: true, "已启用");
            menu.Location = new Point(-32_000, -32_000);
            menu.Show();
            Application.DoEvents();
            menu.Refresh();

            if (menu.Width < 240 || menu.Height < 250 || menu.Region is null)
            {
                return 31;
            }

            using Bitmap bitmap = new(menu.Width, menu.Height, PixelFormat.Format32bppArgb);
            menu.DrawToBitmap(bitmap, menu.ClientRectangle);
            menu.Hide();
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                string resolved = Path.GetFullPath(outputPath);
                string? directory = Path.GetDirectoryName(resolved);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                bitmap.Save(resolved, ImageFormat.Png);
            }

            return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A == 0
                ? 32
                : 0;
        }
        catch (Exception exception) when (exception is ArgumentException
            or ExternalException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return exception.HResult == 0 ? 33 : Math.Abs(exception.HResult % 100) + 33;
        }
    }
}
