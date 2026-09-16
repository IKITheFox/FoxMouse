using FoxMouse.App.UI;
using FoxMouse.Core;
using FoxMouse.Platform.Windows.Processes;

namespace FoxMouse.App;

internal sealed class ProcessPickerDialog : Form
{
    private readonly IProcessCatalog _catalog;
    private readonly HashSet<string> _alreadyExcluded;
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 180 };
    private readonly ImageList _icons = new()
    {
        ColorDepth = ColorDepth.Depth32Bit,
        ImageSize = new Size(24, 24),
        TransparentColor = Color.Transparent,
    };
    private readonly TextBox _search = new()
    {
        Dock = DockStyle.Fill,
        PlaceholderText = UiText.Get("PickerPlaceholder"),
        AccessibleName = UiText.Get("PickerSearchName"),
        AccessibleDescription = UiText.Get("PickerSearchHint"),
    };
    private readonly CheckBox _showBackground = new()
    {
        AutoSize = true,
        Text = UiText.Get("PickerBackground"),
        AccessibleDescription = UiText.Get("PickerBackgroundHint"),
    };
    private readonly Button _refresh = new()
    {
        AutoSize = true,
        Text = UiText.Get("PickerRefresh"),
        AccessibleDescription = UiText.Get("PickerRefreshHint"),
    };
    private readonly ListView _processes = new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = true,
        ShowItemToolTips = true,
        View = View.Details,
        AccessibleName = UiText.Get("PickerApps"),
        AccessibleDescription = UiText.Get("PickerAppsHint"),
    };
    private readonly Label _status = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        AccessibleName = UiText.Get("PickerStatus"),
    };
    private readonly Button _browse = new()
    {
        AutoSize = true,
        Text = UiText.Get("PickerBrowse"),
        AccessibleDescription = UiText.Get("PickerBrowseHint"),
    };
    private readonly Button _add = new()
    {
        AutoSize = true,
        Enabled = false,
        Text = UiText.Get("PickerAdd"),
    };
    private readonly Button _cancel = new()
    {
        AutoSize = true,
        DialogResult = DialogResult.Cancel,
        Text = UiText.Get("Cancel"),
    };
    private IReadOnlyList<ProcessCatalogEntry> _entries = [];
    private CancellationTokenSource? _refreshCancellation;
    private bool _closing;

    public ProcessPickerDialog(
        IProcessCatalog catalog,
        IEnumerable<string>? alreadyExcluded = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _alreadyExcluded = new HashSet<string>(
            ProcessCatalog.MergeExecutableNames(alreadyExcluded, null),
            StringComparer.OrdinalIgnoreCase);

        Text = UiText.Get("PickerTitle");
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(640, 430);
        ClientSize = new Size(760, 520);

        _processes.Columns.Add(UiText.Get("PickerAppColumn"), 210);
        _processes.Columns.Add(UiText.Get("PickerExeColumn"), 170);
        _processes.Columns.Add(UiText.Get("PickerWindowColumn"), 310);
        using (Bitmap defaultIcon = SystemIcons.Application.ToBitmap())
        {
            _icons.Images.Add("default", defaultIcon);
        }
        _processes.SmallImageList = _icons;

        Controls.Add(BuildLayout());
        AcceptButton = _add;
        CancelButton = _cancel;

        _search.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
        };
        _showBackground.CheckedChanged += (_, _) => ApplyFilter();
        _refresh.Click += async (_, _) => await RefreshCatalogAsync();
        _processes.SelectedIndexChanged += (_, _) => _add.Enabled = _processes.SelectedItems.Count > 0;
        _processes.DoubleClick += (_, _) => AddSelected();
        _processes.Resize += (_, _) => ResizeColumns();
        _add.Click += (_, _) => AddSelected();
        _browse.Click += (_, _) => BrowseExecutable();
        Shown += async (_, _) => await RefreshCatalogAsync();
        FormClosing += (_, _) =>
        {
            _closing = true;
            _searchDebounce.Stop();
            _refreshCancellation?.Cancel();
        };

        ApplySystemColors();
        new WindowsThemeService().ApplyTo(this);
    }

    public IReadOnlyList<string> SelectedExecutableNames { get; private set; } = [];

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplySystemColors();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
            _searchDebounce.Dispose();
            _processes.SmallImageList = null;
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TableLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 12),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label introduction = new()
        {
            AutoSize = true,
            Text = UiText.Get("PickerIntro"),
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = UiText.Get("PickerIntroName"),
        };
        toolbar.Controls.Add(introduction, 0, 0);
        toolbar.SetColumnSpan(introduction, 4);

        Label searchLabel = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = UiText.Get("PickerSearch"),
            Margin = new Padding(0, 0, 10, 0),
        };
        toolbar.Controls.Add(searchLabel, 0, 1);
        toolbar.Controls.Add(_search, 1, 1);
        toolbar.Controls.Add(_showBackground, 2, 1);
        toolbar.Controls.Add(_refresh, 3, 1);
        _showBackground.Margin = new Padding(14, 3, 10, 0);

        TableLayoutPanel footer = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 12, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_status, 0, 0);

        FlowLayoutPanel actions = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        actions.Controls.Add(_browse);
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_add);
        footer.Controls.Add(actions, 1, 0);

        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_processes, 0, 1);
        root.Controls.Add(footer, 0, 2);
        return root;
    }

    private async Task RefreshCatalogAsync()
    {
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = _refreshCancellation;
        _refreshCancellation = cancellation;
        previous?.Cancel();

        _refresh.Enabled = false;
        _processes.Enabled = false;
        _status.Text = UiText.Get("PickerLoading");
        UseWaitCursor = true;
        try
        {
            IReadOnlyList<ProcessCatalogEntry> entries = await _catalog.EnumerateAsync(cancellation.Token);
            if (_closing || cancellation.IsCancellationRequested ||
                !ReferenceEquals(_refreshCancellation, cancellation))
            {
                return;
            }

            _entries = entries;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_closing)
            {
                _entries = [];
                _processes.Items.Clear();
                _status.Text = UiText.Get("PickerReadError");
            }
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                if (!_closing && !IsDisposed)
                {
                    _refresh.Enabled = true;
                    _processes.Enabled = true;
                    UseWaitCursor = false;
                }
            }
        }
    }

    private void ApplyFilter()
    {
        if (_closing || IsDisposed)
        {
            return;
        }

        IReadOnlyList<ProcessCatalogEntry> filtered = ProcessCatalog.Filter(
            _entries,
            _search.Text,
            _showBackground.Checked);

        _processes.BeginUpdate();
        try
        {
            _processes.Items.Clear();
            foreach (ProcessCatalogEntry entry in filtered)
            {
                if (_alreadyExcluded.Contains(entry.ExecutableName))
                {
                    continue;
                }

                ListViewItem item = new(entry.DisplayName)
                {
                    ImageKey = GetImageKey(entry),
                    Tag = entry,
                    ToolTipText = entry.ExecutablePath ?? entry.ExecutableName,
                };
                item.SubItems.Add(entry.ExecutableName);
                item.SubItems.Add(entry.WindowTitle);
                _processes.Items.Add(item);
            }
        }
        finally
        {
            _processes.EndUpdate();
        }

        _add.Enabled = false;
        int visibleCount = _processes.Items.Count;
        if (visibleCount == 0 && !_showBackground.Checked && string.IsNullOrWhiteSpace(_search.Text))
        {
            _status.Text = UiText.Get("PickerEmpty");
        }
        else
        {
            _status.Text = UiText.Format("PickerCount", visibleCount);
        }
    }

    private void AddSelected()
    {
        string[] selected = _processes.SelectedItems
            .Cast<ListViewItem>()
            .Select(static item => item.Tag)
            .OfType<ProcessCatalogEntry>()
            .Select(static entry => entry.ExecutableName)
            .ToArray();
        SelectedExecutableNames = ProcessCatalog.MergeExecutableNames(null, selected);
        if (SelectedExecutableNames.Count == 0)
        {
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private string GetImageKey(ProcessCatalogEntry entry)
    {
        string key = entry.ExecutableName.ToUpperInvariant();
        if (_icons.Images.ContainsKey(key))
        {
            return key;
        }

        if (!string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            try
            {
                using Icon? icon = Icon.ExtractAssociatedIcon(entry.ExecutablePath);
                if (icon is not null)
                {
                    using Bitmap bitmap = icon.ToBitmap();
                    _icons.Images.Add(key, bitmap);
                    return key;
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException
                or System.Security.SecurityException)
            {
                // The executable can disappear or deny metadata access after
                // the catalog snapshot. Use the standard application glyph.
            }
        }

        return "default";
    }

    private void BrowseExecutable()
    {
        using OpenFileDialog picker = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true,
            Filter = UiText.Get("PickerFilter"),
            FilterIndex = 1,
            Multiselect = false,
            RestoreDirectory = true,
            Title = UiText.Get("PickerFileTitle"),
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ProcessCatalogEntry? selected;
        try
        {
            selected = _catalog.InspectExecutable(picker.FileName);
        }
        catch (Exception)
        {
            selected = null;
        }
        if (selected is null)
        {
            MessageBox.Show(
                this,
                UiText.Get("PickerInvalidExe"),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_alreadyExcluded.Contains(selected.ExecutableName))
        {
            MessageBox.Show(
                this,
                UiText.Format("AlreadyBlocked", selected.ExecutableName),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SelectedExecutableNames = [selected.ExecutableName];
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ResizeColumns()
    {
        if (_processes.Columns.Count != 3)
        {
            return;
        }

        int available = Math.Max(480, _processes.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        _processes.Columns[0].Width = Math.Max(150, (int)(available * 0.30));
        _processes.Columns[1].Width = Math.Max(140, (int)(available * 0.24));
        _processes.Columns[2].Width = Math.Max(190, available - _processes.Columns[0].Width - _processes.Columns[1].Width);
    }

    private void ApplySystemColors()
    {
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        _search.BackColor = SystemColors.Window;
        _search.ForeColor = SystemColors.WindowText;
        _processes.BackColor = SystemColors.Window;
        _processes.ForeColor = SystemColors.WindowText;
        _status.ForeColor = SystemColors.GrayText;
        _status.Tag = ThemeRole.SecondaryText;
    }
}
