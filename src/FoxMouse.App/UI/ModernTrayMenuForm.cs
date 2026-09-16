using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FoxMouse.App.UI;

/// <summary>
/// A compact Windows 11-style popup for the notification-area icon. A regular
/// Form is used instead of ContextMenuStrip so rounded corners, spacing, focus
/// cues and dark-mode colors remain consistent on every supported Windows build.
/// </summary>
internal sealed class ModernTrayMenuForm : Form
{
    private const int CsDropShadow = 0x00020000;
    private const int BaseWidth = 284;
    private const int OuterPadding = 6;
    private const int StatusHeight = 36;
    private const int ItemHeight = 46;
    private const int SeparatorHeight = 9;
    private const int CornerRadius = 12;

    private readonly WindowsThemeService _themeService;
    private readonly TrayStatusControl _statusControl;
    private readonly TrayMenuItemControl _enabledItem;
    private readonly TrayMenuItemControl _previewItem;
    private readonly TrayMenuItemControl _settingsItem;
    private readonly TrayMenuItemControl _logsItem;
    private readonly TrayMenuItemControl _aboutItem;
    private readonly TrayMenuItemControl _exitItem;
    private readonly TrayMenuItemControl[] _interactiveItems;
    private readonly List<Image> _ownedImages = [];
    private readonly List<int> _separatorCenters = [];
    private SemanticColors _colors;
    private string? _displayLanguage;

    internal ModernTrayMenuForm(WindowsThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _colors = _themeService.Colors;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AccessibleName = FoxMouse.Core.UiText.Get("TrayMenu");
        AccessibleRole = AccessibleRole.MenuPopup;
        DoubleBuffered = true;

        _statusControl = new TrayStatusControl();
        _enabledItem = CreateItem(FoxMouse.Core.UiText.Get("EnableFoxMouse"), glyph: null, FoxMouse.Core.UiText.Get("EnableHint"));
        _previewItem = CreateItem(FoxMouse.Core.UiText.Get("PreviewEnlargement"), TrayMenuGlyph.Preview, FoxMouse.Core.UiText.Get("PreviewHint"));
        _settingsItem = CreateItem(FoxMouse.Core.UiText.Get("SettingsMenu"), TrayMenuGlyph.Settings, FoxMouse.Core.UiText.Get("OpenSettings"));
        _logsItem = CreateItem(FoxMouse.Core.UiText.Get("DiagnosticsMenu"), TrayMenuGlyph.Folder, FoxMouse.Core.UiText.Get("DiagnosticsHint"));
        _aboutItem = CreateItem(FoxMouse.Core.UiText.Get("AboutMenu"), TrayMenuGlyph.About, FoxMouse.Core.UiText.Get("AboutHint"));
        _exitItem = CreateItem(FoxMouse.Core.UiText.Get("Exit"), TrayMenuGlyph.Exit, FoxMouse.Core.UiText.Get("ExitHint"));
        _interactiveItems =
        [
            _enabledItem,
            _previewItem,
            _settingsItem,
            _logsItem,
            _aboutItem,
            _exitItem,
        ];

        Controls.Add(_statusControl);
        Controls.AddRange(_interactiveItems);

        WireAction(_enabledItem, () => EnabledToggleRequested?.Invoke(this, EventArgs.Empty));
        WireAction(_previewItem, () => PreviewRequested?.Invoke(this, EventArgs.Empty));
        WireAction(_settingsItem, () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        WireAction(_logsItem, () => DiagnosticsRequested?.Invoke(this, EventArgs.Empty));
        WireAction(_aboutItem, () => AboutRequested?.Invoke(this, EventArgs.Empty));
        WireAction(_exitItem, () => ExitRequested?.Invoke(this, EventArgs.Empty));

        ApplyTheme();
        RecalculateLayout();
    }

    internal event EventHandler? EnabledToggleRequested;

    internal event EventHandler? PreviewRequested;

    internal event EventHandler? SettingsRequested;

    internal event EventHandler? DiagnosticsRequested;

    internal event EventHandler? AboutRequested;

    internal event EventHandler? ExitRequested;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => false;

    internal void UpdateState(bool enabled, string statusText)
    {
        RefreshLanguage();
        _enabledItem.Checked = enabled;
        _statusControl.StatusText = statusText;
        _enabledItem.Invalidate();
        _statusControl.Invalidate();
    }

    internal void RefreshTheme()
    {
        _themeService.Refresh();
        ApplyTheme();
    }

    internal void ToggleAt(Point anchor)
    {
        if (Visible)
        {
            Hide();
            return;
        }

        ShowAt(anchor);
    }

    internal void ShowAt(Point anchor)
    {
        RefreshLanguage();
        RefreshTheme();
        RecalculateLayout();

        Rectangle workArea = Screen.FromPoint(anchor).WorkingArea;
        int horizontalGap = ScaleLogical(10);
        int verticalGap = ScaleLogical(8);
        int x = anchor.X + Width + horizontalGap <= workArea.Right
            ? anchor.X
            : anchor.X - Width;
        int y = anchor.Y > workArea.Top + (workArea.Height / 2)
            ? anchor.Y - Height - verticalGap
            : anchor.Y + verticalGap;

        x = Math.Clamp(x, workArea.Left + horizontalGap, Math.Max(workArea.Left + horizontalGap, workArea.Right - Width - horizontalGap));
        y = Math.Clamp(y, workArea.Top + verticalGap, Math.Max(workArea.Top + verticalGap, workArea.Bottom - Height - verticalGap));
        Location = new Point(x, y);

        Show();
        _ = SetForegroundWindow(Handle);
        Activate();
        _enabledItem.Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible)
        {
            Hide();
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }

        if (keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End)
        {
            int current = Array.FindIndex(_interactiveItems, item => item.Focused);
            int next = keyData switch
            {
                Keys.Home => 0,
                Keys.End => _interactiveItems.Length - 1,
                Keys.Up => current <= 0 ? _interactiveItems.Length - 1 : current - 1,
                _ => current < 0 || current >= _interactiveItems.Length - 1 ? 0 : current + 1,
            };
            _interactiveItems[next].Focus();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF bounds = new(0.5F, 0.5F, Width - 1F, Height - 1F);
        using GraphicsPath backgroundPath = CreateRoundedRectangle(bounds, ScaleLogical(CornerRadius));
        using SolidBrush background = new(BackColor);
        using Pen border = new(_colors.Border, Math.Max(1F, DeviceDpi / 96F));
        e.Graphics.FillPath(background, backgroundPath);
        e.Graphics.DrawPath(border, backgroundPath);

        using Pen separator = new(_colors.Border, Math.Max(1F, DeviceDpi / 96F));
        int left = ScaleLogical(12);
        int right = Width - ScaleLogical(12);
        foreach (int center in _separatorCenters)
        {
            e.Graphics.DrawLine(separator, left, center, right, center);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RecalculateLayout();
        ApplyTheme();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DwmWindowTheme.TryApply(Handle, _colors);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeImages();
            Region? oldRegion = Region;
            Region = null;
            oldRegion?.Dispose();
        }

        base.Dispose(disposing);
    }

    private TrayMenuItemControl CreateItem(string text, TrayMenuGlyph? glyph, string accessibleDescription)
    {
        TrayMenuItemControl item = new()
        {
            Text = text,
            Glyph = glyph,
            AccessibleName = text,
            AccessibleDescription = accessibleDescription,
        };
        return item;
    }

    private void WireAction(TrayMenuItemControl item, Action action)
    {
        item.Click += (_, _) =>
        {
            Hide();
            action();
        };
    }

    private void ApplyTheme()
    {
        _colors = _themeService.Colors;
        BackColor = _colors.IsDark && !_colors.IsHighContrast
            ? Color.FromArgb(44, 44, 44)
            : _colors.Surface;
        ForeColor = _colors.Text;

        _statusControl.ApplyTheme(_colors, BackColor);
        foreach (TrayMenuItemControl item in _interactiveItems)
        {
            item.ApplyTheme(_colors, BackColor);
        }

        RefreshImages();
        if (IsHandleCreated)
        {
            DwmWindowTheme.TryApply(Handle, _colors);
        }

        Invalidate(invalidateChildren: true);
    }

    private void RefreshImages()
    {
        DisposeImages();
        foreach (TrayMenuItemControl item in _interactiveItems)
        {
            if (item.Glyph is not TrayMenuGlyph glyph)
            {
                item.GlyphImage = null;
                continue;
            }

            Bitmap image = FoxMouseIconFactory.CreateMenuIcon(glyph, _colors.Text, ScaleLogical(20));
            _ownedImages.Add(image);
            item.GlyphImage = image;
        }
    }

    private void DisposeImages()
    {
        foreach (TrayMenuItemControl item in _interactiveItems)
        {
            item.GlyphImage = null;
        }

        foreach (Image image in _ownedImages)
        {
            image.Dispose();
        }

        _ownedImages.Clear();
    }

    private void RecalculateLayout()
    {
        int padding = ScaleLogical(OuterPadding);
        int menuWidth = Math.Max(ScaleLogical(BaseWidth),
            _interactiveItems.Max(item => TextRenderer.MeasureText(item.Text, item.Font).Width) + ScaleLogical(82));
        int contentWidth = menuWidth - (padding * 2);
        int y = padding;

        _separatorCenters.Clear();
        _statusControl.Bounds = new Rectangle(padding, y, contentWidth, ScaleLogical(StatusHeight));
        y += _statusControl.Height;
        AddSeparator(ref y);

        LayoutItem(_enabledItem, contentWidth, padding, ref y);
        LayoutItem(_previewItem, contentWidth, padding, ref y);
        LayoutItem(_settingsItem, contentWidth, padding, ref y);
        AddSeparator(ref y);
        LayoutItem(_logsItem, contentWidth, padding, ref y);
        LayoutItem(_aboutItem, contentWidth, padding, ref y);
        AddSeparator(ref y);
        LayoutItem(_exitItem, contentWidth, padding, ref y);

        Size = new Size(menuWidth, y + padding);
        UpdateRoundedRegion();
    }

    private void RefreshLanguage()
    {
        if (_displayLanguage == FoxMouse.Core.UiText.Language) return;
        _displayLanguage = FoxMouse.Core.UiText.Language;
        AccessibleName = FoxMouse.Core.UiText.Get("TrayMenu");
        _statusControl.AccessibleName = FoxMouse.Core.UiText.Get("TrayStatus");
        string[] labels = ["EnableFoxMouse", "PreviewEnlargement", "SettingsMenu", "DiagnosticsMenu", "AboutMenu", "Exit"];
        string[] hints = ["EnableHint", "PreviewHint", "OpenSettings", "DiagnosticsHint", "AboutHint", "ExitHint"];
        for (int index = 0; index < _interactiveItems.Length; index++)
        {
            TrayMenuItemControl item = _interactiveItems[index];
            item.Text = FoxMouse.Core.UiText.Get(labels[index]);
            item.AccessibleName = item.Text;
            item.AccessibleDescription = FoxMouse.Core.UiText.Get(hints[index]);
        }
        RecalculateLayout();
        Invalidate(true);
    }

    private void LayoutItem(TrayMenuItemControl item, int width, int x, ref int y)
    {
        item.Bounds = new Rectangle(x, y, width, ScaleLogical(ItemHeight));
        item.CornerRadius = ScaleLogical(6);
        item.HorizontalPadding = ScaleLogical(12);
        item.IconSize = ScaleLogical(20);
        item.IconTextGap = ScaleLogical(14);
        y += item.Height;
    }

    private void AddSeparator(ref int y)
    {
        int height = ScaleLogical(SeparatorHeight);
        _separatorCenters.Add(y + (height / 2));
        y += height;
    }

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96D));

    private void UpdateRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using GraphicsPath path = CreateRoundedRectangle(
            new RectangleF(0, 0, Width, Height),
            ScaleLogical(CornerRadius));
        Region replacement = new(path);
        Region? previous = Region;
        Region = replacement;
        previous?.Dispose();
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2F);
        RectangleF arc = new(bounds.X, bounds.Y, diameter, diameter);
        GraphicsPath path = new();
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    private sealed class TrayStatusControl : Control
    {
        private SemanticColors _colors;
        private Color _surface;
        private string _statusText = FoxMouse.Core.UiText.Get("Starting");

        internal TrayStatusControl()
        {
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = FoxMouse.Core.UiText.Get("TrayStatus");
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor,
                true);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = string.IsNullOrWhiteSpace(value) ? FoxMouse.Core.UiText.Get("UnknownStatus") : value;
                AccessibleDescription = FoxMouse.Core.UiText.Format("CurrentStatus", _statusText);
            }
        }

        internal void ApplyTheme(SemanticColors colors, Color surface)
        {
            _colors = colors;
            _surface = surface;
            BackColor = surface;
            ForeColor = colors.SecondaryText;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(_surface);
            float scale = DeviceDpi / 96F;
            int dotSize = Math.Max(6, (int)Math.Round(7 * scale));
            int left = Math.Max(10, (int)Math.Round(13 * scale));
            int dotTop = (Height - dotSize) / 2;
            Color dotColor = _colors.IsHighContrast ? SystemColors.Highlight : _colors.Accent;
            using SolidBrush dotBrush = new(dotColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(dotBrush, left, dotTop, dotSize, dotSize);

            Rectangle textBounds = new(
                left + dotSize + Math.Max(8, (int)Math.Round(9 * scale)),
                0,
                Width - left - dotSize,
                Height);
            TextRenderer.DrawText(
                e.Graphics,
                FoxMouse.Core.UiText.Format("Status", _statusText),
                Font,
                textBounds,
                _colors.SecondaryText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private sealed class TrayMenuItemControl : Control
    {
        private SemanticColors _colors;
        private Color _surface;
        private bool _hovered;
        private bool _pressed;
        private bool _checked;

        internal TrayMenuItemControl()
        {
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.MenuItem;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.Selectable
                | ControlStyles.UserPaint,
                true);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal TrayMenuGlyph? Glyph { get; init; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Image? GlyphImage { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int CornerRadius { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int HorizontalPadding { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int IconSize { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int IconTextGap { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal bool Checked
        {
            get => _checked;
            set
            {
                _checked = value;
                AccessibleDefaultActionDescription = value ? FoxMouse.Core.UiText.Get("Pause") : FoxMouse.Core.UiText.Get("Enable");
            }
        }

        internal void ApplyTheme(SemanticColors colors, Color surface)
        {
            _colors = colors;
            _surface = surface;
            BackColor = surface;
            ForeColor = colors.Text;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Focus();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End
            || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.Handled = true;
                PerformClick();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(_surface);

            bool highlighted = _hovered || Focused || _pressed;
            if (highlighted)
            {
                Color highlight = _colors.IsHighContrast
                    ? SystemColors.Highlight
                    : _pressed
                        ? Blend(_colors.Selection, _colors.Text, 0.08F)
                        : _colors.Selection;
                using GraphicsPath highlightPath = CreateRoundedRectangle(
                    new RectangleF(0, 1, Width, Height - 2),
                    CornerRadius);
                using SolidBrush highlightBrush = new(highlight);
                e.Graphics.FillPath(highlightBrush, highlightPath);
            }

            Color textColor = _colors.IsHighContrast && highlighted
                ? SystemColors.HighlightText
                : _colors.Text;
            int iconLeft = HorizontalPadding;
            int iconTop = (Height - IconSize) / 2;
            if (GlyphImage is not null)
            {
                e.Graphics.DrawImage(GlyphImage, new Rectangle(iconLeft, iconTop, IconSize, IconSize));
            }
            else
            {
                DrawCheckBox(e.Graphics, new Rectangle(iconLeft, iconTop, IconSize, IconSize), textColor);
            }

            int textLeft = iconLeft + IconSize + IconTextGap;
            Rectangle textBounds = new(textLeft, 0, Width - textLeft - HorizontalPadding, Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (Focused && ShowFocusCues && _colors.IsHighContrast)
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, textColor, _surface);
            }
        }

        private void PerformClick()
        {
            OnClick(EventArgs.Empty);
        }

        private void DrawCheckBox(Graphics graphics, Rectangle bounds, Color foreground)
        {
            float scale = DeviceDpi / 96F;
            int inset = Math.Max(1, (int)Math.Round(2 * scale));
            Rectangle box = Rectangle.Inflate(bounds, -inset, -inset);
            Color borderColor = _colors.IsHighContrast ? foreground : _colors.SecondaryText;
            using Pen border = new(borderColor, Math.Max(1.2F, 1.4F * scale));
            using GraphicsPath boxPath = CreateRoundedRectangle(box, Math.Max(2, (int)Math.Round(4 * scale)));
            graphics.DrawPath(border, boxPath);

            if (!_checked)
            {
                return;
            }

            Color fillColor = _colors.IsHighContrast ? SystemColors.Highlight : _colors.Accent;
            using SolidBrush fill = new(fillColor);
            graphics.FillPath(fill, boxPath);
            Color checkColor = _colors.IsHighContrast ? SystemColors.HighlightText : Color.White;
            using Pen check = new(checkColor, Math.Max(1.7F, 2F * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            PointF[] points =
            [
                new(box.Left + (box.Width * 0.23F), box.Top + (box.Height * 0.53F)),
                new(box.Left + (box.Width * 0.43F), box.Top + (box.Height * 0.72F)),
                new(box.Left + (box.Width * 0.78F), box.Top + (box.Height * 0.30F)),
            ];
            graphics.DrawLines(check, points);
        }

        private static Color Blend(Color first, Color second, float amount)
        {
            float clamped = Math.Clamp(amount, 0F, 1F);
            return Color.FromArgb(
                (int)Math.Round(first.A + ((second.A - first.A) * clamped)),
                (int)Math.Round(first.R + ((second.R - first.R) * clamped)),
                (int)Math.Round(first.G + ((second.G - first.G) * clamped)),
                (int)Math.Round(first.B + ((second.B - first.B) * clamped)));
        }
    }
}
