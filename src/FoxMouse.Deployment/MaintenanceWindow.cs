using FoxMouse.Core;
using System.Drawing;
using Microsoft.Win32;

namespace FoxMouse.Deployment;

public sealed class MaintenanceWindow : Form
{
    private static readonly TimeSpan SuccessCloseDelay = TimeSpan.FromMilliseconds(450);
    private const int FriendlyErrorCharacterLimit = 160;

    private DeploymentPaths _paths;
    private readonly string _version;
    private readonly bool _setupMode;
    private readonly IMaintenanceOperationService _operations;
    private readonly IMaintenanceCloseScheduler _closeScheduler;
    private readonly IInstallLocationPicker? _installLocationPicker;
    private readonly MaintenanceSessionController _session = new();
    private readonly ToolTip _locationToolTip = new()
    {
        AutoPopDelay = 12_000,
        InitialDelay = 450,
        ReshowDelay = 100,
        ShowAlways = true,
    };

    private readonly TableLayoutPanel _root = new();
    private readonly TableLayoutPanel _header = new();
    private readonly TableLayoutPanel _headerText = new();
    private readonly PictureBox _logo = new();
    private readonly Label _heading = new();
    private readonly Label _description = new();
    private readonly ComboBox _languagePicker = new FoxMouse.Presentation.ThemeAwareComboBox();
    private readonly Panel _contentHost = new();
    private readonly MaintenanceCardPanel _locationCard = new();
    private readonly TableLayoutPanel _locationLayout = new();
    private readonly Label _locationCaption = new();
    private readonly Label _locationValue = new();
    private readonly Label _locationError = new();
    private readonly MaintenanceButton _changeLocation = new();
    private readonly MaintenanceCheckBox _keepSettings = new();
    private readonly MaintenanceCardPanel _feedbackCard = new();
    private readonly TableLayoutPanel _feedbackLayout = new();
    private readonly Label _feedbackTitle = new();
    private readonly Label _feedbackMessage = new();
    private readonly MaintenanceButton _confirmUninstall = new();
    private readonly MaintenanceButton _cancelUninstall = new();
    private readonly Label _status = new();
    private readonly FlowLayoutPanel _actionButtons = new();
    private readonly MaintenanceButton _primary = new();
    private readonly MaintenanceButton _repair = new();
    private readonly MaintenanceButton _uninstall = new();
    private readonly MaintenanceButton _close = new();
    private readonly MaintenanceButton _retry = new();
    private readonly MaintenanceButton _openLog = new();
    private string? _failureLog;
    private string _idleDescription = string.Empty;

    private Icon? _ownedIcon;
    private Bitmap? _ownedLogo;
    private IDisposable? _scheduledClose;
    private Task? _activeOperation;
    private DeploymentOperation _installOperation = DeploymentOperation.Install;
    private bool _showPrimary;
    private bool _showRepair;
    private bool _showUninstall;
    private bool _installationAbsent;
    private bool _themeRefreshPending;

    public MaintenanceWindow(
        DeploymentEngine engine,
        DeploymentPaths paths,
        string version,
        Func<IDisposablePackage> packageFactory,
        bool setupMode,
        bool keepMaintenanceHost = false,
        bool launchAfterInstall = true,
        IInstallLocationPicker? installLocationPicker = null)
        : this(
            new DeploymentMaintenanceOperationService(
                engine,
                paths,
                version,
                packageFactory,
                keepMaintenanceHost,
                launchAfterInstall),
            paths,
            version,
            setupMode,
            new MaintenanceCloseScheduler(),
            installLocationPicker ?? (setupMode ? new NativeInstallLocationPicker() : null))
    {
    }

    internal MaintenanceWindow(
        IMaintenanceOperationService operations,
        DeploymentPaths paths,
        string version,
        bool setupMode,
        IMaintenanceCloseScheduler closeScheduler,
        IInstallLocationPicker? installLocationPicker = null)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _version = string.IsNullOrWhiteSpace(version)
            ? throw new ArgumentException("Version must not be empty.", nameof(version))
            : version;
        _setupMode = setupMode;
        _closeScheduler = closeScheduler ?? throw new ArgumentNullException(nameof(closeScheduler));
        _installLocationPicker = installLocationPicker;
        if (_operations is IMaintenancePathsUpdater updater)
        {
            updater.UpdatePaths(_paths);
        }

        InitializeWindow();
        BuildLayout();
        WireEvents();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplySystemTheme();
        RefreshInstallationState();
    }

    private void ApplyLanguageChoice()
    {
        if (_session.State != MaintenanceUiState.Idle) return;
        UiText.Configure(_languagePicker.SelectedIndex == 0 ? "zh-CN" : "en-US");
        Text = UiText.Get(_setupMode ? "SetupTitle" : "MaintenanceTitle");
        AccessibleName = Text;
        _logo.AccessibleName = UiText.Get("Logo");
        _locationCard.AccessibleName = UiText.Get("InstallLocation");
        _locationCaption.Text = UiText.Get("InstallLocation");
        _locationValue.AccessibleName = UiText.Get("InstallPath");
        _locationError.AccessibleName = UiText.Get("LocationError");
        _changeLocation.Text = UiText.Get("Change");
        _changeLocation.AccessibleDescription = UiText.Get("ChooseParentHint");
        _keepSettings.Text = UiText.Get("KeepSettings");
        _keepSettings.AccessibleDescription = UiText.Get("KeepSettingsHint");
        _status.AccessibleName = UiText.Get("CurrentStatusLabel");
        _repair.Text = UiText.Get("Repair");
        _uninstall.Text = UiText.Get("Uninstall");
        _confirmUninstall.Text = UiText.Get("ConfirmUninstall");
        _cancelUninstall.Text = UiText.Get("Back");
        foreach (Control control in new Control[] { _changeLocation, _repair, _uninstall, _confirmUninstall, _cancelUninstall })
            control.AccessibleName = control.Text;
        RefreshInstallationState();
    }

    public MaintenanceSessionResult SessionResult => _session.Result;

    /// <summary>
    /// Gets the paths selected for the next install operation. The value only
    /// changes after an injected location picker returns a validated path set.
    /// </summary>
    public DeploymentPaths SelectedPaths => _paths;

    internal MaintenanceUiState UiState => _session.State;

    internal Task? ActiveOperation => _activeOperation;

    /// <summary>
    /// Requests a safe close. A running transaction is allowed to finish and
    /// the window closes automatically immediately afterwards.
    /// </summary>
    public void RequestClose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            _ = BeginInvoke(RequestClose);
            return;
        }

        MaintenanceCloseDisposition disposition = _session.RequestClose();
        if (disposition == MaintenanceCloseDisposition.CloseNow)
        {
            Close();
            return;
        }

        ApplyViewState(UiText.Get("WaitExit"));
    }

    /// <summary>
    /// Drives one operation without pointer input for the isolated installer
    /// window smoke test. Callers must enforce the isolated-deployment gate.
    /// Product UI should use the normal buttons instead.
    /// </summary>
    public Task BeginAutomatedOperationForTestingAsync(DeploymentOperation operation)
    {
        if (InvokeRequired)
        {
            throw new InvalidOperationException(
                "Automated window operations must be started on the window UI thread.");
        }

        return operation switch
        {
            DeploymentOperation.Uninstall => StartOperation(
                DeploymentOperation.Uninstall,
                () => _operations.UninstallAsync(_keepSettings.Checked)),
            DeploymentOperation.Install or DeploymentOperation.Repair or DeploymentOperation.Upgrade =>
                StartOperation(operation, _operations.InstallOrRepairAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
    }

    internal Task WhenIdleAsync() => _activeOperation ?? Task.CompletedTask;

    private void InitializeWindow()
    {
        SuspendLayout();
        Text = _setupMode ? UiText.Get("SetupTitle") : UiText.Get("MaintenanceTitle");
        Name = "MaintenanceWindow";
        AccessibleName = Text;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(
            MaintenanceLayoutMetrics.PreferredClientWidth,
            MaintenanceLayoutMetrics.PreferredClientHeight);
        MinimumSize = SizeFromClientSize(new Size(
            MaintenanceLayoutMetrics.MinimumClientWidth,
            MaintenanceLayoutMetrics.MinimumClientHeight));
        MaximizeBox = false;
        AutoScaleDimensions = new SizeF(
            MaintenanceDpi.DefaultDpi,
            MaintenanceDpi.DefaultDpi);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = false;
        AutoScrollMinSize = Size.Empty;
        KeyPreview = true;
        DoubleBuffered = true;
        Font = MaintenanceTypography.CreateUiFont(10F);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
    }

    private void BuildLayout()
    {
        ConfigureRootLayout();
        ConfigureHeader();
        ConfigureContentHost();
        ConfigureLocationCard();
        ConfigureKeepSettings();
        ConfigureFeedbackCard();
        ConfigureStatus();
        ConfigureActionButtons();

        _root.Controls.Add(_header, 0, 0);
        _root.Controls.Add(_contentHost, 0, 1);
        _root.Controls.Add(_actionButtons, 0, 3);
        _contentHost.Controls.Add(_status);
        _contentHost.Controls.Add(_feedbackCard);
        _contentHost.Controls.Add(_locationCard);
        Controls.Add(_root);
        ResumeLayout(performLayout: true);
    }

    private void ConfigureRootLayout()
    {
        _root.Name = "MaintenanceRootLayout";
        _root.Dock = DockStyle.Fill;
        _root.AutoSize = false;
        _root.Padding = new Padding(MaintenanceLayoutMetrics.WindowPadding);
        _root.ColumnCount = 1;
        _root.RowCount = 4;
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }

    private void ConfigureContentHost()
    {
        _contentHost.Name = "ContentHost";
        _contentHost.Dock = DockStyle.Fill;
        _contentHost.AutoSize = false;
        _contentHost.AutoScroll = false;
        _contentHost.Margin = Padding.Empty;
    }

    private void ConfigureHeader()
    {
        _header.Name = "HeaderLayout";
        _header.Dock = DockStyle.Top;
        _header.AutoSize = true;
        _header.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _header.Margin = new Padding(0, 0, 0, MaintenanceLayoutMetrics.SectionSpacing);
        _header.ColumnCount = 2;
        _header.RowCount = 1;
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _logo.Name = "Logo";
        _logo.Size = new Size(
            MaintenanceLayoutMetrics.LogoSize,
            MaintenanceLayoutMetrics.LogoSize);
        _logo.Margin = Padding.Empty;
        _logo.SizeMode = PictureBoxSizeMode.Zoom;
        _logo.TabStop = false;
        _logo.Visible = false; // Brand mark remains in the native title bar.
        _logo.AccessibleName = UiText.Get("Logo");

        _headerText.Name = "HeaderTextLayout";
        _headerText.Dock = DockStyle.Fill;
        _headerText.AutoSize = true;
        _headerText.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _headerText.Margin = Padding.Empty;
        _headerText.ColumnCount = 1;
        _headerText.RowCount = 3;
        _headerText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _headerText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerText.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _heading.Name = "Heading";
        _heading.AutoSize = true;
        _heading.Dock = DockStyle.Top;
        _heading.Margin = Padding.Empty;
        _heading.Font = MaintenanceTypography.CreateUiFont(20F, FontStyle.Bold);
        _heading.UseMnemonic = false;

        _description.Name = "Description";
        _description.AutoSize = true;
        _description.Dock = DockStyle.Top;
        _description.Margin = new Padding(0, 14, 0, 0);
        _description.UseMnemonic = false;

        _headerText.Controls.Add(_heading, 0, 0);
        _headerText.Controls.Add(_description, 0, 1);
        _languagePicker.Name = "LanguageChoice";
        _languagePicker.AccessibleName = "Language / 语言";
        _languagePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _languagePicker.Items.AddRange(new object[] { "简体中文", "English" });
        _languagePicker.Width = 170;
        // TableLayoutPanel may shrink an unanchored native combo to its small
        // preferred width despite Width, clipping the selected language.
        _languagePicker.MinimumSize = new Size(170, 0);
        _languagePicker.Anchor = AnchorStyles.Left;
        // The native combo's border can exceed GetPreferredSize by two pixels.
        _languagePicker.Margin = new Padding(0, 16, 0, 4);
        _languagePicker.SelectedIndex = UiText.Language == "zh-CN" ? 0 : 1;
        _languagePicker.SelectedIndexChanged += (_, _) => ApplyLanguageChoice();
        _headerText.Controls.Add(_languagePicker, 0, 2);
        _header.Controls.Add(_logo, 0, 0);
        _header.Controls.Add(_headerText, 1, 0);
    }

    private void ConfigureLocationCard()
    {
        _locationCard.Name = "LocationPanel";
        _locationCard.Dock = DockStyle.Top;
        _locationCard.AutoSize = true;
        _locationCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _locationCard.Padding = new Padding(MaintenanceLayoutMetrics.CardPadding);
        _locationCard.Margin = new Padding(0, 0, 0, MaintenanceLayoutMetrics.SectionSpacing);
        _locationCard.AccessibleName = UiText.Get("InstallLocation");

        _locationLayout.Name = "LocationLayout";
        _locationLayout.Dock = DockStyle.Top;
        _locationLayout.AutoSize = true;
        _locationLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _locationLayout.Margin = Padding.Empty;
        _locationLayout.ColumnCount = 2;
        _locationLayout.RowCount = 3;
        _locationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _locationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _locationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _locationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _locationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _locationCaption.Name = "LocationCaption";
        _locationCaption.Text = UiText.Get("InstallLocation");
        _locationCaption.AutoSize = true;
        _locationCaption.Anchor = AnchorStyles.Left;
        _locationCaption.Margin = Padding.Empty;
        _locationCaption.Font = new Font(Font, FontStyle.Bold);
        _locationCaption.UseMnemonic = false;

        _locationValue.Name = "LocationValue";
        _locationValue.Text = _paths.InstallRoot;
        _locationValue.AutoEllipsis = true;
        _locationValue.AutoSize = false;
        _locationValue.Dock = DockStyle.Top;
        _locationValue.Height = 24;
        _locationValue.Margin = new Padding(0, 14, 0, 0);
        _locationValue.UseMnemonic = false;
        _locationValue.AccessibleName = UiText.Get("InstallPath");
        _locationValue.AccessibleDescription = _paths.InstallRoot;
        _locationToolTip.SetToolTip(_locationValue, _paths.InstallRoot);

        ConfigureButton(
            _changeLocation,
            "ChangeLocation",
            UiText.Get("Change"),
            MaintenanceButtonStyle.Standard);
        _changeLocation.AutoSize = true;
        _changeLocation.MinimumSize = new Size(88, 36);
        _changeLocation.Padding = new Padding(12, 5, 12, 5);
        _changeLocation.Margin = new Padding(MaintenanceLayoutMetrics.ControlSpacing, 0, 0, 0);
        _changeLocation.AccessibleDescription = UiText.Get("ChooseParentHint");

        _locationError.Name = "LocationError";
        _locationError.AutoSize = false;
        _locationError.AutoEllipsis = true;
        _locationError.Dock = DockStyle.Top;
        _locationError.Height = 24;
        _locationError.Margin = new Padding(0, 6, 0, 0);
        _locationError.UseMnemonic = false;
        _locationError.Visible = false;
        _locationError.AccessibleRole = AccessibleRole.Alert;
        _locationError.AccessibleName = UiText.Get("LocationError");

        _locationLayout.Controls.Add(_locationCaption, 0, 0);
        _locationLayout.Controls.Add(_changeLocation, 1, 0);
        _locationLayout.Controls.Add(_locationValue, 0, 1);
        _locationLayout.SetColumnSpan(_locationValue, 2);
        _locationLayout.Controls.Add(_locationError, 0, 2);
        _locationLayout.SetColumnSpan(_locationError, 2);
        _locationCard.Controls.Add(_locationLayout);
    }

    private void ConfigureKeepSettings()
    {
        _keepSettings.Name = "KeepSettings";
        _keepSettings.Text = UiText.Get("KeepSettings");
        _keepSettings.Checked = true;
        _keepSettings.Margin = new Padding(0, MaintenanceLayoutMetrics.SectionSpacing, 0, 0);
        _keepSettings.AccessibleDescription = UiText.Get("KeepSettingsHint");
    }

    private void ConfigureFeedbackCard()
    {
        _feedbackCard.Name = "FeedbackPanel";
        _feedbackCard.Dock = DockStyle.Top;
        _feedbackCard.AutoSize = false;
        _feedbackCard.Height = MaintenanceLayoutMetrics.FeedbackCardHeight;
        _feedbackCard.Padding = new Padding(MaintenanceLayoutMetrics.CardPadding);
        _feedbackCard.Margin = new Padding(0, 0, 0, MaintenanceLayoutMetrics.SectionSpacing);
        _feedbackCard.Visible = false;
        _feedbackCard.AccessibleRole = AccessibleRole.Alert;

        _feedbackLayout.Name = "FeedbackLayout";
        _feedbackLayout.Dock = DockStyle.Fill;
        _feedbackLayout.AutoSize = false;
        _feedbackLayout.Margin = Padding.Empty;
        _feedbackLayout.ColumnCount = 1;
        _feedbackLayout.RowCount = 3;
        _feedbackLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _feedbackLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _feedbackLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _feedbackLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _feedbackTitle.Name = "FeedbackTitle";
        _feedbackTitle.AutoSize = true;
        _feedbackTitle.Dock = DockStyle.Top;
        _feedbackTitle.Margin = Padding.Empty;
        _feedbackTitle.Font = new Font(Font, FontStyle.Bold);
        _feedbackTitle.UseMnemonic = false;

        _feedbackMessage.Name = "FeedbackMessage";
        _feedbackMessage.AutoSize = false;
        _feedbackMessage.AutoEllipsis = true;
        _feedbackMessage.Dock = DockStyle.Fill;
        _feedbackMessage.Margin = new Padding(0, 6, 0, 0);
        _feedbackMessage.UseMnemonic = false;

        _feedbackLayout.Controls.Add(_feedbackTitle, 0, 0);
        _feedbackLayout.Controls.Add(_keepSettings, 0, 1);
        _feedbackLayout.Controls.Add(_feedbackMessage, 0, 2);
        _feedbackCard.Controls.Add(_feedbackLayout);
    }

    private void ConfigureStatus()
    {
        _status.Name = "Status";
        _status.AutoSize = true;
        _status.Dock = DockStyle.Top;
        _status.Margin = Padding.Empty;
        _status.UseMnemonic = false;
        _status.AccessibleRole = AccessibleRole.StaticText;
        _status.AccessibleName = UiText.Get("CurrentStatusLabel");
    }

    private void ConfigureActionButtons()
    {
        _actionButtons.Name = "ActionButtons";
        _actionButtons.AutoSize = true;
        _actionButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _actionButtons.Dock = DockStyle.Fill;
        _actionButtons.FlowDirection = FlowDirection.RightToLeft;
        _actionButtons.WrapContents = false;
        _actionButtons.Margin = new Padding(0, MaintenanceLayoutMetrics.SectionSpacing, 0, 0);

        ConfigureButton(_primary, "PrimaryAction", UiText.Get("Install"), MaintenanceButtonStyle.Accent);
        ConfigureButton(_repair, "RepairAction", UiText.Get("Repair"), MaintenanceButtonStyle.Accent);
        ConfigureButton(_uninstall, "UninstallAction", UiText.Get("Uninstall"), MaintenanceButtonStyle.Standard);
        ConfigureButton(_close, "CloseAction", UiText.Get("Close"), MaintenanceButtonStyle.Standard);
        ConfigureButton(_retry, "RetryAction", TextFor("重试", "Retry"), MaintenanceButtonStyle.Accent);
        ConfigureButton(_openLog, "OpenLogAction", TextFor("打开日志", "Open log"), MaintenanceButtonStyle.Standard);
        ConfigureButton(
            _confirmUninstall,
            "ConfirmUninstall",
            UiText.Get("ConfirmUninstall"),
            MaintenanceButtonStyle.Danger);
        ConfigureButton(
            _cancelUninstall,
            "CancelUninstall",
            UiText.Get("Back"),
            MaintenanceButtonStyle.Standard);
        _primary.TabIndex = 0;
        _repair.TabIndex = 0;
        _uninstall.TabIndex = 1;
        _close.TabIndex = 2;
        _confirmUninstall.TabIndex = 0;
        _cancelUninstall.TabIndex = 1;

        _actionButtons.Controls.Add(_close);
        _actionButtons.Controls.Add(_openLog);
        _actionButtons.Controls.Add(_retry);
        _actionButtons.Controls.Add(_uninstall);
        _actionButtons.Controls.Add(_repair);
        _actionButtons.Controls.Add(_primary);
        _actionButtons.Controls.Add(_cancelUninstall);
        _actionButtons.Controls.Add(_confirmUninstall);
        CancelButton = _close;
    }

    private static void ConfigureButton(
        MaintenanceButton button,
        string name,
        string text,
        MaintenanceButtonStyle style)
    {
        button.Name = name;
        button.Text = text;
        button.VisualStyle = style;
        button.Margin = new Padding(MaintenanceLayoutMetrics.ControlSpacing, 0, 0, 0);
        button.AccessibleName = text;
    }

    private void WireEvents()
    {
        _primary.Click += (_, _) => _ = StartOperation(
            _installOperation,
            _operations.InstallOrRepairAsync);
        _repair.Click += (_, _) => _ = StartOperation(
            DeploymentOperation.Repair,
            _operations.InstallOrRepairAsync);
        _uninstall.Click += (_, _) => ShowUninstallConfirmation();
        _confirmUninstall.Click += (_, _) => _ = StartOperation(
            DeploymentOperation.Uninstall,
            () => _operations.UninstallAsync(_keepSettings.Checked));
        _cancelUninstall.Click += (_, _) => CancelUninstallConfirmation();
        _changeLocation.Click += (_, _) => TryChangeInstallLocation();
        _locationCard.Click += (_, _) => TryChangeInstallLocation();
        _locationLayout.Click += (_, _) => TryChangeInstallLocation();
        _locationCaption.Click += (_, _) => TryChangeInstallLocation();
        _locationValue.Click += (_, _) => TryChangeInstallLocation();
        _close.Click += (_, _) => RequestClose();
        _retry.Click += (_, _) =>
        {
            if (_session.Result.Operation == DeploymentOperation.Uninstall) ShowUninstallConfirmation();
            else _ = StartOperation(_session.Result.Operation ?? _installOperation, _operations.InstallOrRepairAsync);
        };
        _openLog.Click += (_, _) =>
        {
            if (_failureLog is not null && File.Exists(_failureLog))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{_failureLog}\"") { UseShellExecute = true });
        };
        _headerText.SizeChanged += (_, _) => UpdateWrappingWidths();
        _feedbackLayout.SizeChanged += (_, _) => UpdateWrappingWidths();
        _locationLayout.SizeChanged += (_, _) => UpdateWrappingWidths();
        SizeChanged += (_, _) => UpdateWrappingWidths();
    }

    private void TryChangeInstallLocation()
    {
        if (!_changeLocation.Visible || !_changeLocation.Enabled || _installLocationPicker is null)
        {
            return;
        }

        ClearLocationError();
        try
        {
            DeploymentPaths? selectedPaths = _installLocationPicker.PickInstallLocation(this, _paths);
            if (selectedPaths is null)
            {
                return;
            }

            if (_operations is not IMaintenancePathsUpdater updater)
            {
                throw new InvalidOperationException(UiText.Get("LocationUnsupported"));
            }

            updater.UpdatePaths(selectedPaths);
            _paths = selectedPaths;
            UpdateLocationPresentation();
            _status.Text = UiText.Get("LocationUpdated");
            _status.AccessibleDescription = _status.Text;
            _status.ForeColor = CurrentPalette.SecondaryText;
        }
        catch (Exception exception)
        {
            ShowLocationError(exception);
        }
    }

    private void UpdateLocationPresentation()
    {
        _locationValue.Text = _paths.InstallRoot;
        _locationValue.AccessibleDescription = _paths.InstallRoot;
        _locationToolTip.SetToolTip(_locationValue, _paths.InstallRoot);
        _locationCard.AccessibleDescription = _paths.InstallRoot;
        _locationCard.Invalidate(invalidateChildren: true);
    }

    private void ClearLocationError()
    {
        _locationError.Visible = false;
        _locationError.Text = string.Empty;
        _locationError.AccessibleDescription = string.Empty;
        _locationToolTip.SetToolTip(_locationError, null);
    }

    private void ShowLocationError(Exception exception)
    {
        string message = FriendlyError(exception);
        string details = ErrorDetails(exception);
        _locationError.Text = UiText.Format("LocationChangeError", message);
        _locationError.AccessibleDescription = details;
        _locationToolTip.SetToolTip(_locationError, details);
        _locationError.Visible = true;
        _status.Text = UiText.Get("LocationUnchanged");
        _status.AccessibleDescription = _locationError.Text;
        _status.ForeColor = CurrentPalette.Error;
        PerformStableLayoutAndRepaint();
    }

    private void RefreshInstallationState()
    {
        InstallationStatus state = _operations.GetStatus();
        bool installed = state.Kind != InstallationKind.Absent;
        _installationAbsent = !installed;
        bool managedSameVersion = state.Kind == InstallationKind.Managed &&
            string.Equals(state.Version, _version, StringComparison.OrdinalIgnoreCase);

        if (!installed)
        {
            _heading.Text = _setupMode ? UiText.Get("InstallHeading") : UiText.Get("NotInstalled");
            _description.Text = _setupMode
                ? UiText.Format("InstallVersion", FoxMouse.Core.ProductRelease.DisplayInstalledVersion(_version))
                : UiText.Get("NoInstallation");
            _primary.Text = UiText.Get("Install");
            _primary.AccessibleName = _primary.Text;
            _installOperation = DeploymentOperation.Install;
            _showPrimary = _setupMode;
            _showRepair = false;
            _showUninstall = false;
        }
        else
        {
            _heading.Text = state.Kind == InstallationKind.Legacy ? UiText.Get("UpgradeHeading") : UiText.Get("MaintainHeading");
            _description.Text = state.Kind == InstallationKind.Legacy
                ? UiText.Get("LegacyUpgradeHint")
                : UiText.Format("InstalledVersion", FoxMouse.Core.ProductRelease.DisplayInstalledVersion(state.Version ?? UiText.Get("Unknown")));
            _primary.Text = managedSameVersion ? UiText.Get("Repair") : UiText.Get("Upgrade");
            _primary.AccessibleName = _primary.Text;
            _installOperation = managedSameVersion
                ? DeploymentOperation.Repair
                : DeploymentOperation.Upgrade;
            _showPrimary = !managedSameVersion;
            _showRepair = managedSameVersion;
            _showUninstall = true;
        }

        _idleDescription = _description.Text;
        UpdateLocationPresentation();
        ApplyViewState(UiText.Get("Ready"));
        if (IsHandleCreated)
        {
            BeginInvoke(UpdateWrappingWidths);
        }
    }

    private void ShowUninstallConfirmation()
    {
        _session.BeginConfirmation();
        ApplyViewState(string.Empty);
        _confirmUninstall.Focus();
    }

    /// <summary>
    /// Shows the non-destructive uninstall confirmation state for an isolated
    /// window-capture smoke test. This method never starts an uninstall.
    /// </summary>
    public void ShowUninstallConfirmationForTesting()
    {
        if (InvokeRequired)
        {
            throw new InvalidOperationException(
                "The confirmation state must be entered on the window UI thread.");
        }

        if (!_showUninstall || _session.State is not (MaintenanceUiState.Idle or MaintenanceUiState.Failed))
        {
            throw new InvalidOperationException(
                "Uninstall confirmation is only available for an idle installed product.");
        }

        ShowUninstallConfirmation();
    }

    private void CancelUninstallConfirmation()
    {
        _session.CancelConfirmation();
        ApplyViewState(UiText.Get("Ready"));
        _uninstall.Focus();
    }

    private Task StartOperation(
        DeploymentOperation operation,
        Func<Task<DeploymentOutcome>> action)
    {
        if (_session.State is MaintenanceUiState.Running or MaintenanceUiState.SucceededClosing or MaintenanceUiState.SucceededAwaitingConfirmation)
        {
            return _activeOperation ?? Task.CompletedTask;
        }

        _activeOperation = RunOperationAsync(operation, action);
        return _activeOperation;
    }

    private async Task RunOperationAsync(
        DeploymentOperation operation,
        Func<Task<DeploymentOutcome>> action)
    {
        if (!_session.BeginOperation(operation))
        {
            return;
        }

        ApplyViewState(OperationInProgressText(operation));
        try
        {
            DeploymentOutcome outcome = await action().ConfigureAwait(true);
            _session.Complete(outcome);
            bool awaitsConfirmation = _session.State == MaintenanceUiState.SucceededAwaitingConfirmation;
            ApplyViewState(awaitsConfirmation ? string.Empty :
                outcome.Operation == DeploymentOperation.Uninstall
                    ? UiText.Get("UninstallDoneClosing")
                    : UiText.Get("DoneClosing"));

            if (_session.CloseRequested)
            {
                BeginInvoke(Close);
            }
            else if (!awaitsConfirmation)
            {
                _scheduledClose?.Dispose();
                _scheduledClose = _closeScheduler.Schedule(this, SuccessCloseDelay, Close);
            }
            else
            {
                _close.Focus();
            }
        }
        catch (Exception exception)
        {
            _session.Fail(exception);
            try
            {
                string logDirectory = Path.Combine(_paths.SettingsRoot, "InstallerLogs");
                Directory.CreateDirectory(logDirectory);
                _failureLog = Path.Combine(logDirectory, $"setup-{Guid.NewGuid():N}.log");
                File.WriteAllText(_failureLog, $"{DateTimeOffset.Now:O}\n{operation}\n{exception}");
            }
            catch (IOException) { _failureLog = null; }
            catch (UnauthorizedAccessException) { _failureLog = null; }
            ApplyViewState(UiText.Get("FailedRecovered"));
            if (_session.CloseRequested)
            {
                BeginInvoke(Close);
            }
            else
            {
                _close.Focus();
            }
        }
    }

    private static string OperationInProgressText(DeploymentOperation operation) => operation switch
    {
        DeploymentOperation.Install => UiText.Get("Installing"),
        DeploymentOperation.Repair => UiText.Get("Repairing"),
        DeploymentOperation.Upgrade => UiText.Get("Upgrading"),
        DeploymentOperation.Uninstall => UiText.Get("Uninstalling"),
        _ => UiText.Get("Processing"),
    };

    private void ApplyViewState(string statusText)
    {
        bool failed = _session.State == MaintenanceUiState.Failed;
        bool idle = _session.State == MaintenanceUiState.Idle;
        bool confirming = _session.State == MaintenanceUiState.ConfirmingUninstall;
        bool running = _session.State == MaintenanceUiState.Running;
        bool succeeded = _session.State == MaintenanceUiState.SucceededClosing;
        bool completed = _session.State == MaintenanceUiState.SucceededAwaitingConfirmation;
        _languagePicker.Enabled = idle;
        _languagePicker.Visible = idle && _installationAbsent && _setupMode;

        SuspendLayout();
        _root.SuspendLayout();
        _contentHost.SuspendLayout();
        _actionButtons.SuspendLayout();

        _locationCard.Visible = idle || confirming || running;
        _feedbackCard.Visible = confirming || failed || completed;
        _primary.Visible = idle && _showPrimary;
        _repair.Visible = idle && _showRepair;
        _uninstall.Visible = idle && _showUninstall;
        _retry.Visible = failed;
        _openLog.Visible = failed && _failureLog is not null;
        _retry.Text = TextFor("重试", "Retry");
        _openLog.Text = TextFor("打开日志", "Open log");
        _confirmUninstall.Visible = confirming;
        _cancelUninstall.Visible = confirming;
        _keepSettings.Visible = confirming;
        _keepSettings.Enabled = confirming;
        bool canChangeLocation = idle && _installationAbsent && _setupMode &&
            _installLocationPicker is not null;
        _changeLocation.Visible = canChangeLocation;
        _changeLocation.Enabled = canChangeLocation;
        _locationCard.Cursor = canChangeLocation ? Cursors.Hand : Cursors.Default;
        _locationLayout.Cursor = _locationCard.Cursor;
        _locationCaption.Cursor = _locationCard.Cursor;
        _locationValue.Cursor = _locationCard.Cursor;

        _close.Visible = !confirming && !succeeded;
        _close.Text = completed ? UiText.Get("OK") : running && _session.CloseRequested ? UiText.Get("CloseWhenDone") : UiText.Get("Close");
        _close.AccessibleName = _close.Text;
        _close.Enabled = true;
        _close.VisualStyle = completed ? MaintenanceButtonStyle.Accent : MaintenanceButtonStyle.Standard;
        AcceptButton = failed ? _retry : completed ? _close : confirming
            ? _confirmUninstall
            : (idle || failed) && _showPrimary
                ? _primary
                : (idle || failed) && _showRepair
                    ? _repair
                    : null;
        CancelButton = confirming ? _cancelUninstall : _close;

        _feedbackCard.IsError = failed;
        _feedbackLayout.BackColor = CurrentPalette.Window;
        if (idle)
        {
            _description.Text = _idleDescription;
            _heading.Text = _installationAbsent ? UiText.Get("InstallHeading") :
                _installOperation == DeploymentOperation.Upgrade ? UiText.Get("UpgradeHeading") : UiText.Get("MaintainHeading");
        }
        if (running)
        {
            _heading.Text = OperationInProgressText(_session.Result.Operation ?? _installOperation);
            _description.Text = TextFor("请稍候，操作完成后将显示结果。", "Please wait. The result will appear when finished.");
        }
        if (completed)
        {
            _heading.Text = _session.Result.Operation switch
            {
                DeploymentOperation.Repair => TextFor("修复完成", "Repair complete"),
                DeploymentOperation.Uninstall => TextFor("卸载完成", "Uninstall complete"),
                DeploymentOperation.Upgrade => TextFor("升级完成", "Upgrade complete"),
                _ => TextFor("安装完成", "Installation complete"),
            };
            _description.Text = _session.Result.Operation == DeploymentOperation.Uninstall
                ? TextFor("FoxMouse 已从此电脑移除。", "FoxMouse has been removed from this computer.")
                : $"FoxMouse {FoxMouse.Core.ProductRelease.DisplayInstalledVersion(_version)}";
            _feedbackTitle.Text = "✓  " + (_session.Result.Operation == DeploymentOperation.Uninstall
                ? (_keepSettings.Checked ? TextFor("个人设置和诊断日志已保留。", "Settings and diagnostic logs were retained.") : TextFor("个人设置和诊断日志已移除。", "Settings and diagnostic logs were removed."))
                : TextFor("操作已成功完成。", "The operation completed successfully."));
            _feedbackMessage.Text = UiText.Get("ClickOKToClose");
            _feedbackMessage.AccessibleDescription = _feedbackMessage.Text;
            _feedbackCard.AccessibleName = _heading.Text;
            _locationToolTip.SetToolTip(_feedbackMessage, null);
        }
        else if (confirming)
        {
            _heading.Text = TextFor("卸载 FoxMouse", "Uninstall FoxMouse");
            _description.Text = TextFor("确定要卸载 FoxMouse 吗？", "Are you sure you want to uninstall FoxMouse?");
            _feedbackTitle.Text = string.Empty;
            _feedbackMessage.Text = TextFor("卸载前将恢复系统光标。", "The system cursor will be restored before uninstalling.");
            _feedbackMessage.AccessibleDescription = _feedbackMessage.Text;
            _locationToolTip.SetToolTip(_feedbackMessage, null);
            _feedbackCard.AccessibleName = UiText.Get("UninstallConfirmation");
        }
        else if (failed)
        {
            _heading.Text = TextFor("暂未完成", "Not completed");
            _description.Text = TextFor("请查看错误详情后重试。", "Review the error details and try again.");
            string details = ErrorDetails(_session.Result.Error);
            _feedbackTitle.Text = UiText.Get("OperationIncomplete");
            _feedbackMessage.Text = FriendlyError(_session.Result.Error);
            _feedbackMessage.AccessibleDescription = details;
            _locationToolTip.SetToolTip(_feedbackMessage, details);
            _feedbackCard.AccessibleName = UiText.Get("OperationError");
        }

        _status.Text = statusText;
        _status.Visible = !string.IsNullOrWhiteSpace(statusText);
        _status.AccessibleDescription = statusText;
        UseWaitCursor = running;
        _status.ForeColor = failed
            ? CurrentPalette.Error
            : CurrentPalette.SecondaryText;
        _feedbackTitle.ForeColor = _feedbackCard.IsError
            ? CurrentPalette.Error
            : CurrentPalette.Text;

        _actionButtons.ResumeLayout(performLayout: true);
        _contentHost.ResumeLayout(performLayout: true);
        _root.ResumeLayout(performLayout: true);
        ResumeLayout(performLayout: true);
        PerformStableLayoutAndRepaint();
    }

    private static string TextFor(string chinese, string english) => UiText.Language == "zh-CN" ? chinese : english;

    private static string FriendlyError(Exception? error)
    {
        string message = ErrorDetails(error);
        string normalized = string.Join(
            " ",
            message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= FriendlyErrorCharacterLimit)
        {
            return normalized;
        }

        return normalized[..(FriendlyErrorCharacterLimit - 1)] + "…";
    }

    private static string ErrorDetails(Exception? error)
    {
        string message = error?.Message?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(message)
            ? UiText.Get("UnknownError")
            : message;
    }

    private MaintenanceThemePalette CurrentPalette { get; set; }

    private void UpdateWrappingWidths()
    {
        if (IsDisposed)
        {
            return;
        }

        int headerWidth = Math.Max(160, _headerText.ClientSize.Width);
        _description.MaximumSize = new Size(headerWidth, 0);
        _feedbackMessage.MaximumSize = Size.Empty;
    }

    private void PerformStableLayoutAndRepaint()
    {
        if (IsDisposed)
        {
            return;
        }

        PerformLayout();
        _root.PerformLayout();
        _contentHost.PerformLayout();
        UpdateWrappingWidths();
        Invalidate(invalidateChildren: true);
        _root.Invalidate(invalidateChildren: true);
        _contentHost.Invalidate(invalidateChildren: true);
        Update();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PerformStableLayoutAndRepaint();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MaintenanceWindowChrome.TryApply(Handle, CurrentPalette);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_session.RequestClose() == MaintenanceCloseDisposition.Deferred)
        {
            e.Cancel = true;
            ApplyViewState(UiText.Get("WaitExit"));
        }

        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is WmThemeChanged or WmSettingChange or WmSysColorChange or
            WmDwmColorizationColorChanged)
        {
            QueueSystemThemeRefresh();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _scheduledClose?.Dispose();
            _scheduledClose = null;
            _locationToolTip.Dispose();
            _logo.Image = null;
            _ownedLogo?.Dispose();
            _ownedLogo = null;
            Icon = null;
            _ownedIcon?.Dispose();
            _ownedIcon = null;
        }

        base.Dispose(disposing);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs)
    {
        QueueSystemThemeRefresh();
    }

    private void QueueSystemThemeRefresh()
    {
        if (IsDisposed || !IsHandleCreated || _themeRefreshPending)
        {
            return;
        }

        try
        {
            _themeRefreshPending = true;
            _ = BeginInvoke(() =>
            {
                _themeRefreshPending = false;
                if (!IsDisposed)
                {
                    ApplySystemTheme();
                }
            });
        }
        catch (InvalidOperationException)
        {
            _themeRefreshPending = false;
            // The handle was destroyed between notification and dispatch.
        }
    }

    private void ApplySystemTheme()
    {
        DeploymentBrandVariant variant = DeploymentBranding.DetectCurrentVariant();
        CurrentPalette = MaintenanceThemePalette.Resolve(
            variant,
            Color.FromArgb(246, 107, 43));

        BackColor = CurrentPalette.Window;
        ForeColor = CurrentPalette.Text;
        _root.BackColor = CurrentPalette.Window;
        _header.BackColor = CurrentPalette.Window;
        _headerText.BackColor = CurrentPalette.Window;
        _languagePicker.BackColor = CurrentPalette.Card;
        _languagePicker.ForeColor = CurrentPalette.Text;
        _contentHost.BackColor = CurrentPalette.Window;
        _locationLayout.BackColor = CurrentPalette.Window;
        _feedbackLayout.BackColor = CurrentPalette.Window;
        _actionButtons.BackColor = CurrentPalette.Window;
        _keepSettings.BackColor = CurrentPalette.Window;

        _heading.ForeColor = CurrentPalette.Text;
        _description.ForeColor = CurrentPalette.SecondaryText;
        _locationCaption.ForeColor = CurrentPalette.Text;
        _locationValue.ForeColor = CurrentPalette.SecondaryText;
        _locationError.ForeColor = CurrentPalette.Error;
        _feedbackTitle.ForeColor = _feedbackCard.IsError
            ? CurrentPalette.Error
            : CurrentPalette.Text;
        _feedbackMessage.ForeColor = CurrentPalette.Text;
        _status.ForeColor = _session.State == MaintenanceUiState.Failed
            ? CurrentPalette.Error
            : CurrentPalette.SecondaryText;

        foreach (IMaintenanceThemedControl control in new IMaintenanceThemedControl[]
        {
            _locationCard,
            _keepSettings,
            _feedbackCard,
            _changeLocation,
            _confirmUninstall,
            _cancelUninstall,
            _primary,
            _repair,
            _uninstall,
            _close,
            _retry,
            _openLog,
        })
        {
            control.ApplyPalette(CurrentPalette);
        }

        if (IsHandleCreated)
        {
            MaintenanceWindowChrome.TryApply(Handle, CurrentPalette);
        }

        ReplaceBrandAssets(variant);
        PerformStableLayoutAndRepaint();
    }

    private void ReplaceBrandAssets(DeploymentBrandVariant variant)
    {
        Icon replacementIcon = DeploymentBranding.CreateIcon(variant);
        Bitmap replacementLogo = DeploymentBranding.CreateMark(variant);
        Icon? previousIcon = _ownedIcon;
        Bitmap? previousLogo = _ownedLogo;

        _ownedIcon = replacementIcon;
        _ownedLogo = replacementLogo;
        Icon = replacementIcon;
        _logo.Image = replacementLogo;

        previousIcon?.Dispose();
        previousLogo?.Dispose();
    }

    private sealed class DeploymentMaintenanceOperationService :
        IMaintenanceOperationService,
        IMaintenancePathsUpdater
    {
        private readonly DeploymentEngine _engine;
        private DeploymentPaths _paths;
        private readonly string _version;
        private readonly Func<IDisposablePackage> _packageFactory;
        private readonly bool _keepMaintenanceHost;
        private readonly bool _launchAfterInstall;

        public DeploymentMaintenanceOperationService(
            DeploymentEngine engine,
            DeploymentPaths paths,
            string version,
            Func<IDisposablePackage> packageFactory,
            bool keepMaintenanceHost,
            bool launchAfterInstall)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _version = version;
            _packageFactory = packageFactory ?? throw new ArgumentNullException(nameof(packageFactory));
            _keepMaintenanceHost = keepMaintenanceHost;
            _launchAfterInstall = launchAfterInstall;
        }

        public InstallationStatus GetStatus() => _engine.GetStatus(_paths);

        public void UpdatePaths(DeploymentPaths paths) =>
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));

        public Task<DeploymentOutcome> InstallOrRepairAsync() => Task.Run(() =>
        {
            using IDisposablePackage package = _packageFactory();
            return _engine.InstallOrRepair(new InstallRequest
            {
                Paths = _paths,
                PackagePath = package.PackagePath,
                Version = _version,
                LaunchAfterInstall = _launchAfterInstall,
                InitialLanguage = UiText.Language,
            });
        });

        public Task<DeploymentOutcome> UninstallAsync(bool keepSettings) => Task.Run(() =>
            _engine.Uninstall(new UninstallRequest
            {
                Paths = _paths,
                KeepSettings = keepSettings,
                KeepMaintenanceHost = _keepMaintenanceHost,
            }));
    }

    private const int WmSettingChange = 0x001A;
    private const int WmSysColorChange = 0x0015;
    private const int WmThemeChanged = 0x031A;
    private const int WmDwmColorizationColorChanged = 0x0320;
}

/// <summary>
/// Selects and validates a complete path set for an interactive install.
/// Returning <see langword="null"/> means that the user cancelled the picker.
/// </summary>
public interface IInstallLocationPicker
{
    DeploymentPaths? PickInstallLocation(IWin32Window owner, DeploymentPaths currentPaths);
}

internal interface IMaintenancePathsUpdater
{
    void UpdatePaths(DeploymentPaths paths);
}

internal sealed class NativeInstallLocationPicker : IInstallLocationPicker
{
    public DeploymentPaths? PickInstallLocation(IWin32Window owner, DeploymentPaths currentPaths)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(currentPaths);

        string initialParent = Path.GetDirectoryName(
            currentPaths.InstallRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ??
            currentPaths.InstallRoot;
        using FolderBrowserDialog dialog = new()
        {
            Description = UiText.Get("ChooseParent"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = initialParent,
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return null;
        }

        string installRoot = InstallLocationPolicy.ResolveInstallRoot(
            dialog.SelectedPath,
            currentPaths);
        return currentPaths.WithInstallRoot(installRoot);
    }
}

public interface IDisposablePackage : IDisposable
{
    string PackagePath { get; }
}
