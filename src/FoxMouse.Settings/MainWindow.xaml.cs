using System.ComponentModel;
using System.Runtime.InteropServices;
using FoxMouse.Settings.Pages;
using FoxMouse.Settings.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace FoxMouse.Settings;

public sealed partial class MainWindow : Window
{
    private const int SwRestore = 9;
    private ThemeSettings? _themeSettings;
    private SettingsPageKind _requestedPage;
    private bool _allowClose;
    private bool _closeDialogOpen;
    private bool _isLoaded;
    private bool _globalExitRequested;
    private readonly TaskCompletionSource _closedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task RequestGlobalExitAsync()
    {
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            _globalExitRequested = true;
            ActivateAndBringToFront();
            // Window.Close bypasses AppWindow.Closing. Run the same save
            // decision explicitly before a programmatic shutdown.
            await ConfirmCloseAsync();
        }))
        {
            _closedCompletion.TrySetResult();
        }
        return _closedCompletion.Task;
    }

    public MainWindow(SettingsActivationRequest activation)
    {
        ViewModel = SettingsHostViewModel.CreateDefault(Program.SettingsRootOverride);
        _requestedPage = activation.Page;
        InitializeComponent();
        Title = FoxMouse.Core.UiText.Get("SettingsTitle");
        RootGrid.DataContext = ViewModel;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        CenterOnPrimaryWorkArea();

        InitializeThemeListener();
        Closed += MainWindow_Closed;
        AppWindow.Closing += AppWindow_Closing;
        RootGrid.Loaded += RootGrid_Loaded;
        ApplyBackdrop();
    }

    public SettingsHostViewModel ViewModel { get; }

    internal nint WindowHandle => WindowNative.GetWindowHandle(this);

    internal void ActivateAndBringToFront()
    {
        Activate();
        if (WindowHandle != 0)
        {
            _ = ShowWindow(WindowHandle, SwRestore);
            _ = SetForegroundWindow(WindowHandle);
        }
    }

    internal void NavigateTo(SettingsPageKind page)
    {
        _requestedPage = page;
        if (!_isLoaded)
        {
            return;
        }

        NavigationViewItem? item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => PageFromTag(candidate.Tag as string) == page);
        if (item is not null)
        {
            NavView.SelectedItem = item;
        }

        NavigateFrame(page);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        try
        {
            await ViewModel.LoadAsync();
            _isLoaded = true;
            NavigateTo(_requestedPage);
        }
        catch (Exception exception)
        {
            ViewModel.ReportError(FoxMouse.Core.UiText.Format("ReadSettingsError", exception.Message));
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError(FoxMouse.Core.UiText.Format("RestoreSettingsError", exception.Message));
        }
    }

    private async Task<bool> SaveAsync()
    {
        try
        {
            await ViewModel.SaveAsync();
            return true;
        }
        catch (Exception exception)
        {
            ViewModel.ReportError(FoxMouse.Core.UiText.SettingsSaveFailure(exception));
            return false;
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            SettingsPageKind page = PageFromTag(item.Tag as string);
            _requestedPage = page;
            NavigateFrame(page);
        }
    }

    private void NavigateFrame(SettingsPageKind page)
    {
        Type pageType = page switch
        {
            SettingsPageKind.Exclusions => typeof(ExclusionsPage),
            SettingsPageKind.About => typeof(AboutPage),
            _ => typeof(GeneralPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, ViewModel);
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !ViewModel.IsDirty)
        {
            return;
        }

        args.Cancel = true;
        await ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        if (_closeDialogOpen)
        {
            return;
        }

        if (_allowClose || !ViewModel.IsDirty)
        {
            _allowClose = true;
            Close();
            return;
        }

        _closeDialogOpen = true;
        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = FoxMouse.Core.UiText.Get("SaveChanges"),
            Content = FoxMouse.Core.UiText.Get("UnsavedSettings"),
            PrimaryButtonText = FoxMouse.Core.UiText.Get("SaveAndExit"),
            SecondaryButtonText = FoxMouse.Core.UiText.Get("DontSave"),
            CloseButtonText = FoxMouse.Core.UiText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            SettingsCloseChoice choice = result switch
            {
                ContentDialogResult.Primary => SettingsCloseChoice.Save,
                ContentDialogResult.Secondary => SettingsCloseChoice.Discard,
                _ => SettingsCloseChoice.Cancel,
            };
            bool saved = choice != SettingsCloseChoice.Save || await SaveAsync();
            if (SettingsClosePolicy.ShouldClose(choice, _globalExitRequested, saved))
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private void ThemeSettings_Changed(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(ApplyBackdrop);

    private void ApplyBackdrop()
    {
        try
        {
            if (!IsHighContrastEnabled() && MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
                FallbackBackground.Visibility = Visibility.Collapsed;
                return;
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Mica unavailable: {exception.Message}");
        }

        SystemBackdrop = null;
        FallbackBackground.Visibility = Visibility.Visible;
    }

    private void CenterOnPrimaryWorkArea()
    {
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        RectInt32 workArea = area.WorkArea;
        double scale = Math.Max(96u, GetDpiForWindow(WindowHandle)) / 96d;
        AppWindow.Resize(new SizeInt32(
            Math.Min((int)Math.Round(1100 * scale), Math.Max(320, workArea.Width - 32)),
            Math.Min((int)Math.Round(780 * scale), Math.Max(320, workArea.Height - 32))));
        SizeInt32 size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2)));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closedCompletion.TrySetResult();
        if (_themeSettings is not null)
        {
            _themeSettings.Changed -= ThemeSettings_Changed;
            _themeSettings = null;
        }
        AppWindow.Closing -= AppWindow_Closing;
        ViewModel.Dispose();
    }

    private static SettingsPageKind PageFromTag(string? tag) => tag switch
    {
        "exclusions" => SettingsPageKind.Exclusions,
        "about" => SettingsPageKind.About,
        _ => SettingsPageKind.General,
    };

    private void InitializeThemeListener()
    {
        try
        {
            _themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
            _themeSettings.Changed += ThemeSettings_Changed;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // Theme resources still update automatically. This listener only
            // controls whether the optional Mica backdrop should be enabled.
            System.Diagnostics.Debug.WriteLine($"Theme listener unavailable: {exception.Message}");
            _themeSettings = null;
        }
    }

    private bool IsHighContrastEnabled()
    {
        if (_themeSettings is not null)
        {
            return _themeSettings.HighContrast;
        }

        HighContrast highContrast = new() { Size = (uint)Marshal.SizeOf<HighContrast>() };
        return SystemParametersInfo(
                action: 0x0042,
                parameter: highContrast.Size,
                ref highContrast,
                update: 0) &&
            (highContrast.Flags & 0x00000001) != 0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref HighContrast highContrast,
        uint update);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }
}
