using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace FoxMouse.Settings.Branding;

public static class BrandLogoAssets
{
    public const string LightLogoUri = "ms-appx:///Assets/Branding/FoxMouse.Mark.Light.png";
    public const string DarkLogoUri = "ms-appx:///Assets/Branding/FoxMouse.Mark.Dark.png";
    public const string HighContrastBlackLogoUri =
        "ms-appx:///Assets/Branding/FoxMouse.Mark.HighContrast.Black.png";
    public const string HighContrastWhiteLogoUri =
        "ms-appx:///Assets/Branding/FoxMouse.Mark.HighContrast.White.png";

    public static BitmapImage CreateThemeImage(FrameworkElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new BitmapImage(GetThemeUri(owner.ActualTheme));
    }

    public static IDisposable ObserveThemeChanges(FrameworkElement owner, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(refresh);
        return new ThemeWatcher(owner, refresh);
    }

    public static Uri GetThemeUri(ElementTheme actualTheme)
    {
        if (IsHighContrastEnabled())
        {
            double? luminance = TryGetForegroundLuminance();
            return new Uri(
                luminance is not null
                    ? luminance >= 0.5
                        ? HighContrastWhiteLogoUri
                        : HighContrastBlackLogoUri
                    : actualTheme == ElementTheme.Dark
                    ? HighContrastWhiteLogoUri
                    : HighContrastBlackLogoUri);
        }

        return new Uri(actualTheme == ElementTheme.Dark ? DarkLogoUri : LightLogoUri);
    }

    private static bool IsHighContrastEnabled()
    {
        try
        {
            return new AccessibilitySettings().HighContrast;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static double? TryGetForegroundLuminance()
    {
        try
        {
            Windows.UI.Color foreground = new UISettings().GetColorValue(UIColorType.Foreground);
            return ((0.2126 * foreground.R) +
                    (0.7152 * foreground.G) +
                    (0.0722 * foreground.B)) / 255d;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private sealed class ThemeWatcher : IDisposable
    {
        private readonly FrameworkElement _owner;
        private readonly Action _refresh;
        private readonly AccessibilitySettings? _accessibility;
        private readonly UISettings? _uiSettings;
        private bool _disposed;

        internal ThemeWatcher(FrameworkElement owner, Action refresh)
        {
            _owner = owner;
            _refresh = refresh;
            _owner.ActualThemeChanged += HandleActualThemeChanged;
            try
            {
                AccessibilitySettings accessibility = new();
                accessibility.HighContrastChanged += HandleHighContrastChanged;
                _accessibility = accessibility;
            }
            catch (COMException)
            {
                // Some unpackaged WinUI hosts do not expose this WinRT service.
            }

            try
            {
                UISettings uiSettings = new();
                uiSettings.ColorValuesChanged += HandleColorValuesChanged;
                _uiSettings = uiSettings;
            }
            catch (COMException)
            {
                // ActualThemeChanged remains available as the safe fallback.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.ActualThemeChanged -= HandleActualThemeChanged;
            TryUnsubscribe(() =>
            {
                if (_accessibility is not null)
                {
                    _accessibility.HighContrastChanged -= HandleHighContrastChanged;
                }
            });
            TryUnsubscribe(() =>
            {
                if (_uiSettings is not null)
                {
                    _uiSettings.ColorValuesChanged -= HandleColorValuesChanged;
                }
            });
        }

        private void HandleActualThemeChanged(FrameworkElement sender, object args) => QueueRefresh();

        private void HandleHighContrastChanged(AccessibilitySettings sender, object args) => QueueRefresh();

        private void HandleColorValuesChanged(UISettings sender, object args) => QueueRefresh();

        private void QueueRefresh()
        {
            if (_disposed)
            {
                return;
            }

            if (_owner.DispatcherQueue.HasThreadAccess)
            {
                _refresh();
                return;
            }

            _ = _owner.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed)
                {
                    _refresh();
                }
            });
        }

        private static void TryUnsubscribe(Action unsubscribe)
        {
            try
            {
                unsubscribe();
            }
            catch (COMException)
            {
                // The WinRT service can disappear while the app is shutting down.
            }
        }
    }
}
