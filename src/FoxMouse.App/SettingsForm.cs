using System.Globalization;
using FoxMouse.Core;
using FoxMouse.App.UI;
using FoxMouse.Platform.Windows.Processes;

namespace FoxMouse.App;

internal sealed class SettingsForm : Form
{
    private const int MaximumExcludedProcesses = 128;

    private readonly IProcessCatalog _processCatalog;
    private readonly FoxMouseSettings _originalSettings;
    private readonly ToolTip _configurationToolTip = new()
    {
        AutoPopDelay = 10_000,
        InitialDelay = 500,
        ReshowDelay = 100,
        ShowAlways = true,
    };
    private readonly CheckBox _enabled = new()
    {
        Text = UiText.Get("FallbackEnable"),
        AutoSize = true,
        AccessibleDescription = UiText.Get("FallbackEnableHint"),
    };
    private readonly ComboBox _mode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        AccessibleName = UiText.Get("FallbackMode"),
        AccessibleDescription = UiText.Get("FallbackModeHint"),
    };
    private readonly TrackBar _sensitivity = new()
    {
        Minimum = 0,
        Maximum = 100,
        TickFrequency = 10,
        Dock = DockStyle.Fill,
        AccessibleName = UiText.Get("FallbackSensitivity"),
        AccessibleDescription = UiText.Get("FallbackSensitivityHint"),
    };
    private readonly Label _sensitivityValue = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        AccessibleName = UiText.Get("FallbackCurrentSensitivity"),
    };
    private readonly LocalizedTrimmedNumericUpDown _maxScale = new()
    {
        Increment = 0.1m,
        Minimum = 1.5m,
        Maximum = 6.0m,
        Width = 112,
        AccessibleName = UiText.Get("FallbackScale"),
        AccessibleDescription = UiText.Get("FallbackScaleHint"),
    };
    private readonly CheckBox _startWithWindows = new()
    {
        Text = UiText.Get("FallbackStartup"),
        AutoSize = true,
        AccessibleDescription = UiText.Get("FallbackStartupHint"),
    };
    private readonly CheckBox _disableWhileDragging = new()
    {
        Text = UiText.Get("FallbackDragging"),
        AutoSize = true,
        AccessibleDescription = UiText.Get("FallbackDraggingHint"),
    };
    private readonly CheckBox _pauseInFullscreen = new()
    {
        Text = UiText.Get("FallbackFullscreen"),
        AutoSize = true,
        AccessibleDescription = UiText.Get("FallbackFullscreenHint"),
    };
    private readonly ListBox _excludedProcesses = new()
    {
        Dock = DockStyle.Fill,
        Height = 132,
        IntegralHeight = false,
        HorizontalScrollbar = true,
        SelectionMode = SelectionMode.MultiExtended,
        AccessibleName = UiText.Get("FallbackBlocked"),
        AccessibleDescription = UiText.Get("FallbackBlockedHint"),
    };
    private readonly Button _addExcluded = new()
    {
        Text = UiText.Get("FallbackAdd"),
        AutoSize = true,
        AccessibleDescription = UiText.Get("FallbackAddHint"),
    };
    private readonly Button _removeExcluded = new()
    {
        Text = UiText.Get("FallbackRemove"),
        AutoSize = true,
        Enabled = false,
    };
    private readonly Label _exclusionCount = new()
    {
        AutoSize = true,
        AccessibleName = UiText.Get("FallbackBlockedCountName"),
    };
    private readonly Button _save = new()
    {
        Text = UiText.Get("FallbackSave"),
        DialogResult = DialogResult.OK,
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button _cancel = new()
    {
        Text = UiText.Get("Cancel"),
        DialogResult = DialogResult.Cancel,
        AutoSize = true,
    };
    private bool _loadingSettings;

    public SettingsForm(FoxMouseSettings settings)
        : this(settings, new ProcessCatalog())
    {
    }

    internal SettingsForm(FoxMouseSettings settings, IProcessCatalog processCatalog)
    {
        _processCatalog = processCatalog ?? throw new ArgumentNullException(nameof(processCatalog));
        _originalSettings = SettingsNormalizer.Normalize(settings);

        Text = UiText.Get("SettingsTitle");
        ClientSize = new Size(680, 700);
        MinimumSize = new Size(560, 580);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoValidate = AutoValidate.EnablePreventFocusChange;
        Font = new Font("Segoe UI", 9F);
        AccessibleName = UiText.Get("SettingsTitle");
        AccessibleDescription = UiText.Get("FallbackSettingsHint");

        _mode.Items.AddRange([
            UiText.Get("FallbackNative"),
            UiText.Get("FallbackRing"),
        ]);
        ConfigureToolTips();
        _enabled.CheckedChanged += (_, _) => MarkDirty();
        _mode.SelectedIndexChanged += (_, _) => MarkDirty();
        _sensitivity.ValueChanged += (_, _) =>
        {
            UpdateSensitivityLabel();
            MarkDirty();
        };
        _maxScale.ValueChanged += (_, _) => MarkDirty();
        _startWithWindows.CheckedChanged += (_, _) => MarkDirty();
        _disableWhileDragging.CheckedChanged += (_, _) => MarkDirty();
        _pauseInFullscreen.CheckedChanged += (_, _) => MarkDirty();
        _excludedProcesses.SelectedIndexChanged += (_, _) =>
            _removeExcluded.Enabled = _excludedProcesses.SelectedIndices.Count > 0;
        _excludedProcesses.KeyDown += HandleExcludedProcessesKeyDown;
        _addExcluded.Click += (_, _) => AddExcludedProcesses();
        _removeExcluded.Click += (_, _) => RemoveSelectedProcesses();

        Controls.Add(BuildRootLayout());
        AcceptButton = _save;
        CancelButton = _cancel;
        LoadSettings(settings);
        ApplySystemColors();
    }

    public FoxMouseSettings Result => SettingsNormalizer.Normalize(_originalSettings with
    {
        Enabled = _enabled.Checked,
        Mode = _mode.SelectedIndex == 1 ? CursorEffectMode.Compatibility : CursorEffectMode.HighFidelity,
        Sensitivity = _sensitivity.Value / 100d,
        MaxScale = (double)_maxScale.Value,
        StartWithWindows = _startWithWindows.Checked,
        DisableWhileDragging = _disableWhileDragging.Checked,
        PauseInFullscreen = _pauseInFullscreen.Checked,
        ExcludedProcesses = _excludedProcesses.Items.Cast<string>().ToArray(),
    });

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplySystemColors();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _configurationToolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control BuildRootLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Panel scrollHost = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            AccessibleName = UiText.Get("FallbackContent"),
        };
        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 22, 24, 22),
            ColumnCount = 1,
            RowCount = 5,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label title = new()
        {
            Text = "FoxMouse",
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 18),
        };
        content.Controls.Add(title, 0, 0);
        content.Controls.Add(BuildAppearanceSection(), 0, 1);
        content.Controls.Add(BuildTriggerSection(), 0, 2);
        content.Controls.Add(BuildBehaviorSection(), 0, 3);
        content.Controls.Add(BuildExclusionsSection(), 0, 4);
        foreach (Control section in content.Controls)
        {
            if (section is GroupBox)
            {
                section.Margin = new Padding(0, 0, 0, 14);
            }
        }

        scrollHost.Controls.Add(content);
        root.Controls.Add(scrollHost, 0, 0);
        root.Controls.Add(BuildFooter(), 0, 1);
        return root;
    }

    private GroupBox BuildAppearanceSection()
    {
        TableLayoutPanel grid = CreateSectionGrid();
        grid.RowCount = 2;
        grid.Controls.Add(_enabled, 0, 0);
        grid.SetColumnSpan(_enabled, 2);
        AddLabeledControl(grid, 1, UiText.Get("FallbackModeLabel"), _mode);
        return CreateSection(UiText.Get("FallbackAppearance"), grid);
    }

    private GroupBox BuildTriggerSection()
    {
        TableLayoutPanel grid = CreateSectionGrid();
        grid.RowCount = 2;

        TableLayoutPanel sensitivityRow = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        sensitivityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sensitivityRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sensitivityRow.Controls.Add(_sensitivity, 0, 0);
        sensitivityRow.Controls.Add(_sensitivityValue, 1, 0);
        _sensitivityValue.Margin = new Padding(12, 0, 0, 0);

        AddLabeledControl(grid, 0, UiText.Get("FallbackSensitivityLabel"), sensitivityRow);
        AddLabeledControl(grid, 1, UiText.Get("FallbackScaleLabel"), _maxScale);
        return CreateSection(UiText.Get("FallbackTriggerSection"), grid);
    }

    private GroupBox BuildBehaviorSection()
    {
        FlowLayoutPanel options = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        options.Controls.Add(_startWithWindows);
        options.Controls.Add(_disableWhileDragging);
        options.Controls.Add(_pauseInFullscreen);
        foreach (Control option in options.Controls)
        {
            option.Margin = new Padding(0, 3, 0, 7);
        }

        return CreateSection(UiText.Get("FallbackBehaviorSection"), options);
    }

    private GroupBox BuildExclusionsSection()
    {
        TableLayoutPanel grid = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label explanation = new()
        {
            AutoSize = true,
            Text = UiText.Get("FallbackBlockedIntro"),
            ForeColor = SystemColors.GrayText,
            Tag = ThemeRole.SecondaryText,
            Margin = new Padding(0, 0, 0, 8),
        };
        grid.Controls.Add(explanation, 0, 0);
        grid.SetColumnSpan(explanation, 2);
        grid.Controls.Add(_excludedProcesses, 0, 1);

        FlowLayoutPanel listActions = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(12, 0, 0, 0),
        };
        listActions.Controls.Add(_addExcluded);
        listActions.Controls.Add(_removeExcluded);
        _addExcluded.Margin = new Padding(0, 0, 0, 6);
        _removeExcluded.Margin = Padding.Empty;
        grid.Controls.Add(listActions, 1, 1);
        grid.Controls.Add(_exclusionCount, 0, 2);
        _exclusionCount.Margin = new Padding(0, 7, 0, 0);
        return CreateSection(UiText.Get("FallbackBlockedSection"), grid);
    }

    private Control BuildFooter()
    {
        TableLayoutPanel footer = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(18, 12, 18, 12),
            ColumnCount = 2,
            RowCount = 1,
            AccessibleName = UiText.Get("FallbackActions"),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label hint = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = UiText.Get("FallbackSaveHint"),
            ForeColor = SystemColors.GrayText,
            Tag = ThemeRole.SecondaryText,
        };
        FlowLayoutPanel actions = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_save);
        footer.Controls.Add(hint, 0, 0);
        footer.Controls.Add(actions, 1, 0);
        return footer;
    }

    private static TableLayoutPanel CreateSectionGrid()
    {
        TableLayoutPanel grid = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static GroupBox CreateSection(string title, Control content)
    {
        GroupBox section = new()
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 10, 14, 14),
            Tag = ThemeRole.Card,
        };
        content.Dock = DockStyle.Top;
        section.Controls.Add(content);
        return section;
    }

    private static void AddLabeledControl(
        TableLayoutPanel layout,
        int row,
        string text,
        Control control)
    {
        Label caption = new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 18, 8),
        };
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 5, 0, 5);
        layout.Controls.Add(caption, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private void ConfigureToolTips()
    {
        _configurationToolTip.SetToolTip(
            _enabled,
            UiText.Get("FallbackPreviewHint"));
        _configurationToolTip.SetToolTip(
            _mode,
            UiText.Get("FallbackModeHint"));
        _configurationToolTip.SetToolTip(
            _sensitivity,
            UiText.Get("FallbackSensitivityHint"));
        _configurationToolTip.SetToolTip(
            _maxScale,
            UiText.Get("FallbackScaleHint"));
        _configurationToolTip.SetToolTip(
            _startWithWindows,
            UiText.Get("FallbackStartupHint"));
        _configurationToolTip.SetToolTip(
            _disableWhileDragging,
            UiText.Get("FallbackDraggingHint"));
        _configurationToolTip.SetToolTip(
            _pauseInFullscreen,
            UiText.Get("FallbackFullscreenHint"));
    }

    private void LoadSettings(FoxMouseSettings settings)
    {
        _loadingSettings = true;
        try
        {
            FoxMouseSettings normalized = SettingsNormalizer.Normalize(settings);
            _enabled.Checked = normalized.Enabled;
            _mode.SelectedIndex = normalized.Mode == CursorEffectMode.Compatibility ? 1 : 0;
            _sensitivity.Value = (int)Math.Round(normalized.Sensitivity * 100);
            _maxScale.Value = (decimal)normalized.MaxScale;
            _startWithWindows.Checked = normalized.StartWithWindows;
            _disableWhileDragging.Checked = normalized.DisableWhileDragging;
            _pauseInFullscreen.Checked = normalized.PauseInFullscreen;

            _excludedProcesses.Items.Clear();
            _excludedProcesses.Items.AddRange(
                ProcessCatalog.MergeExecutableNames(normalized.ExcludedProcesses, null));
            UpdateSensitivityLabel();
            UpdateExclusionState();
        }
        finally
        {
            _loadingSettings = false;
            _save.Enabled = false;
        }
    }

    private void AddExcludedProcesses()
    {
        string[] existing = _excludedProcesses.Items.Cast<string>().ToArray();
        using ProcessPickerDialog picker = new(_processCatalog, existing);
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string[] merged = ProcessCatalog.MergeExecutableNames(existing, picker.SelectedExecutableNames);
        bool truncated = merged.Length > MaximumExcludedProcesses;
        if (truncated)
        {
            merged = merged[..MaximumExcludedProcesses];
        }

        _excludedProcesses.BeginUpdate();
        try
        {
            _excludedProcesses.Items.Clear();
            _excludedProcesses.Items.AddRange(merged);
        }
        finally
        {
            _excludedProcesses.EndUpdate();
        }

        UpdateExclusionState();
        MarkDirty();
        if (truncated)
        {
            MessageBox.Show(
                this,
                UiText.Format("FallbackLimit", MaximumExcludedProcesses),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void RemoveSelectedProcesses()
    {
        int[] selected = _excludedProcesses.SelectedIndices.Cast<int>()
            .OrderByDescending(static index => index)
            .ToArray();
        foreach (int index in selected)
        {
            _excludedProcesses.Items.RemoveAt(index);
        }

        UpdateExclusionState();
        MarkDirty();
    }

    private void HandleExcludedProcessesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Delete || _excludedProcesses.SelectedIndices.Count == 0)
        {
            return;
        }

        RemoveSelectedProcesses();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void UpdateSensitivityLabel()
    {
        string description = _sensitivity.Value switch
        {
            < 34 => UiText.Get("Low"),
            < 67 => UiText.Get("Medium"),
            _ => UiText.Get("High"),
        };
        _sensitivityValue.Text = $"{description}（{_sensitivity.Value}%）";
        _sensitivity.AccessibleDefaultActionDescription = UiText.Format("FallbackCurrentValue", description, _sensitivity.Value);
    }

    private void UpdateExclusionState()
    {
        int count = _excludedProcesses.Items.Count;
        _exclusionCount.Text = UiText.Format("FallbackBlockedCount", count, MaximumExcludedProcesses);
        _addExcluded.Enabled = count < MaximumExcludedProcesses;
        _removeExcluded.Enabled = _excludedProcesses.SelectedIndices.Count > 0;
    }

    private void MarkDirty()
    {
        if (!_loadingSettings)
        {
            _save.Enabled = true;
        }
    }

    // Colors are intentionally centralized so a future theme service can
    // replace this method without changing layout or behavior code.
    private void ApplySystemColors()
    {
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        _mode.BackColor = SystemColors.Window;
        _mode.ForeColor = SystemColors.WindowText;
        _excludedProcesses.BackColor = SystemColors.Window;
        _excludedProcesses.ForeColor = SystemColors.WindowText;
    }
}

internal sealed class LocalizedTrimmedNumericUpDown : NumericUpDown
{
    private bool _updatingEditText;

    public LocalizedTrimmedNumericUpDown() => DecimalPlaces = 2;

    protected override void UpdateEditText()
    {
        // Assigning Text can synchronously ask NumericUpDown to refresh the
        // edit field again. Guard the complete formatting operation so the
        // trimmed representation does not recursively re-enter this override.
        if (_updatingEditText)
        {
            return;
        }

        _updatingEditText = true;
        try
        {
            base.UpdateEditText();
            string formatted = Value.ToString("0.##", CultureInfo.CurrentCulture);
            if (!string.Equals(Text, formatted, StringComparison.CurrentCulture))
            {
                Text = formatted;
            }
        }
        finally
        {
            _updatingEditText = false;
        }
    }
}
