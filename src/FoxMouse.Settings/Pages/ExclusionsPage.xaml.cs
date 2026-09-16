using FoxMouse.Settings.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FoxMouse.Settings.Pages;

public sealed partial class ExclusionsPage : Page
{
    private bool _initialRefreshStarted;

    public ExclusionsPage() => InitializeComponent();

    private SettingsHostViewModel? ViewModel => DataContext as SettingsHostViewModel;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter as SettingsHostViewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialRefreshStarted || ViewModel is null)
        {
            return;
        }

        _initialRefreshStarted = true;
        await RefreshProcessesAsync();
    }

    private void ProcessSearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && ViewModel is not null)
        {
            ViewModel.ProcessSearch = sender.Text;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            await ViewModel.RefreshProcessesAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError(FoxMouse.Core.UiText.Format("RefreshProcessesError", exception.Message));
        }
    }

    private void AddProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RunningProcessItemViewModel process })
        {
            ViewModel?.AddExclusion(process.ExecutableName);
        }
    }

    private void RemoveProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ExcludedProcessItemViewModel process })
        {
            ViewModel?.RemoveExclusion(process.ExecutableName);
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Application.Current is not App app || app.MainWindowHandle == 0)
        {
            return;
        }

        try
        {
            FileOpenPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, app.MainWindowHandle);
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ViewModel.AddExclusion(file.Name);
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportError(FoxMouse.Core.UiText.Format("ChooseAppError", exception.Message));
        }
    }
}
