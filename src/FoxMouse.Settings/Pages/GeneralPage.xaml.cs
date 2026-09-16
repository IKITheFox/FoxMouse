using System.Globalization;
using FoxMouse.Settings.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Globalization.NumberFormatting;

namespace FoxMouse.Settings.Pages;

public sealed partial class GeneralPage : Page
{
    public GeneralPage()
    {
        InitializeComponent();
        MaxScaleNumberBox.NumberFormatter = new LocalizedTrimmedNumberFormatter(CultureInfo.CurrentCulture);
        MaxScaleNumberBox.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            // Run after NumberBox finishes committing its internal text editor.
            if (!double.IsFinite(MaxScaleNumberBox.Value) && DataContext is SettingsHostViewModel viewModel)
                MaxScaleNumberBox.Value = viewModel.MaxScale;
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter as SettingsHostViewModel;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsHostViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.PreviewAsync();
        }
        catch (Exception exception)
        {
            viewModel.ReportError(FoxMouse.Core.UiText.Format("PreviewError", exception.Message));
        }
    }
}

internal sealed class LocalizedTrimmedNumberFormatter(CultureInfo culture) : INumberFormatter2, INumberParser
{
    private readonly CultureInfo _culture = culture ?? throw new ArgumentNullException(nameof(culture));

    public string FormatDouble(double value) => value.ToString("0.##", _culture);

    public string FormatInt(long value) => value.ToString(_culture);

    public string FormatUInt(ulong value) => value.ToString(_culture);

    public double? ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, _culture, out double value)
            ? value
            : null;

    public long? ParseInt(string text) =>
        long.TryParse(text, NumberStyles.Integer, _culture, out long value)
            ? value
            : null;

    public ulong? ParseUInt(string text) =>
        ulong.TryParse(text, NumberStyles.Integer, _culture, out ulong value)
            ? value
            : null;
}
