using FoxMouse.Settings.Branding;
using FoxMouse.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FoxMouse.Settings.Pages;

public sealed partial class AboutPage : Page
{
    private IDisposable? _brandingThemeSubscription;

    public AboutPage()
    {
        InitializeComponent();
        RefreshBrandLogo();
        Loaded += HandleLoaded;
        Unloaded += HandleUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter as SettingsHostViewModel;
    }

    private void RefreshBrandLogo() =>
        BrandLogoImage.Source = BrandLogoAssets.CreateThemeImage(BrandLogoImage);

    private void HandleLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _brandingThemeSubscription?.Dispose();
        _brandingThemeSubscription = BrandLogoAssets.ObserveThemeChanges(
            BrandLogoImage,
            RefreshBrandLogo);
        RefreshBrandLogo();
    }

    private void HandleUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _brandingThemeSubscription?.Dispose();
        _brandingThemeSubscription = null;
    }
}
