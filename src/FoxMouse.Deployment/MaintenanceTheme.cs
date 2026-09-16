using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FoxMouse.Deployment;

internal static class MaintenanceLayoutMetrics
{
    public const int MinimumClientWidth = 600;
    public const int MinimumClientHeight = 440;
    public const int PreferredClientWidth = 680;
    public const int PreferredClientHeight = 520;
    public const int WindowPadding = 32;
    public const int SectionSpacing = 24;
    public const int ControlSpacing = 12;
    public const int CardPadding = 0;
    public const int FeedbackCardHeight = 140;
    public const int LogoSize = 72;
    public const int ButtonHeight = 40;
    public const int ButtonMinimumWidth = 112;
    public const int CornerRadius = 8;
}

internal static class MaintenanceTypography
{
    private const string PreferredCjkFamily = "Microsoft YaHei UI";

    internal static Font CreateUiFont(float size, FontStyle style = FontStyle.Regular)
    {
        try
        {
            Font preferred = new(PreferredCjkFamily, size, style, GraphicsUnit.Point);
            if (string.Equals(preferred.Name, PreferredCjkFamily, StringComparison.OrdinalIgnoreCase))
            {
                return preferred;
            }

            preferred.Dispose();
        }
        catch (ArgumentException)
        {
            // Fall through to the current Windows message font.
        }

        return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
    }
}

internal static class MaintenanceDpi
{
    internal const int DefaultDpi = 96;

    internal static int Scale(int logicalPixels, int dpi)
    {
        if (logicalPixels == 0)
        {
            return 0;
        }

        int effectiveDpi = dpi > 0 ? dpi : DefaultDpi;
        return Math.Max(1, (int)Math.Round(
            logicalPixels * effectiveDpi / (double)DefaultDpi,
            MidpointRounding.AwayFromZero));
    }

    internal static float Scale(float logicalPixels, int dpi)
    {
        int effectiveDpi = dpi > 0 ? dpi : DefaultDpi;
        return logicalPixels * effectiveDpi / DefaultDpi;
    }
}

internal readonly record struct MaintenanceThemePalette(
    bool IsDark,
    bool IsHighContrast,
    Color Window,
    Color Surface,
    Color Card,
    Color Text,
    Color SecondaryText,
    Color DisabledText,
    Color Border,
    Color Accent,
    Color AccentText,
    Color Error,
    Color ErrorSurface)
{
    internal static MaintenanceThemePalette Resolve(
        DeploymentBrandVariant variant,
        Color accent)
    {
        bool highContrast = variant is DeploymentBrandVariant.HighContrastBlack or
            DeploymentBrandVariant.HighContrastWhite;
        if (highContrast)
        {
            return new MaintenanceThemePalette(
                IsDark: SystemColors.Window.GetBrightness() < 0.5F,
                IsHighContrast: true,
                Window: SystemColors.Window,
                Surface: SystemColors.Control,
                Card: SystemColors.Control,
                Text: SystemColors.WindowText,
                SecondaryText: SystemColors.WindowText,
                DisabledText: SystemColors.GrayText,
                Border: SystemColors.WindowFrame,
                Accent: SystemColors.Highlight,
                AccentText: SystemColors.HighlightText,
                Error: SystemColors.WindowText,
                ErrorSurface: SystemColors.Window);
        }

        if (variant == DeploymentBrandVariant.Dark)
        {
            return new MaintenanceThemePalette(
                IsDark: true,
                IsHighContrast: false,
                Window: Color.FromArgb(32, 32, 32),
                Surface: Color.FromArgb(32, 32, 32),
                Card: Color.FromArgb(44, 44, 44),
                Text: Color.FromArgb(245, 245, 245),
                SecondaryText: Color.FromArgb(196, 196, 196),
                DisabledText: Color.FromArgb(148, 148, 148),
                Border: Color.FromArgb(82, 82, 82),
                Accent: accent,
                AccentText: ChooseReadableText(accent),
                Error: Color.FromArgb(255, 153, 164),
                ErrorSurface: Color.FromArgb(68, 35, 39));
        }

        return new MaintenanceThemePalette(
            IsDark: false,
            IsHighContrast: false,
                Window: Color.FromArgb(249, 249, 249),
            Surface: Color.FromArgb(249, 249, 249),
            Card: Color.FromArgb(239, 240, 242),
            Text: Color.FromArgb(27, 27, 27),
            SecondaryText: Color.FromArgb(92, 92, 92),
            DisabledText: Color.FromArgb(112, 112, 112),
            Border: Color.FromArgb(216, 216, 216),
            Accent: accent,
            AccentText: ChooseReadableText(accent),
            Error: Color.FromArgb(164, 38, 44),
            ErrorSurface: Color.FromArgb(253, 236, 238));
    }

    private static Color ChooseReadableText(Color background)
    {
        double luminance =
            (0.2126D * LinearChannel(background.R)) +
            (0.7152D * LinearChannel(background.G)) +
            (0.0722D * LinearChannel(background.B));
        double blackContrast = (luminance + 0.05D) / 0.05D;
        double whiteContrast = 1.05D / (luminance + 0.05D);
        return blackContrast >= whiteContrast ? Color.Black : Color.White;
    }

    private static double LinearChannel(byte channel)
    {
        double value = channel / 255D;
        return value <= 0.04045D
            ? value / 12.92D
            : Math.Pow((value + 0.055D) / 1.055D, 2.4D);
    }
}

internal enum MaintenanceButtonStyle
{
    Standard,
    Accent,
    Danger,
}

internal interface IMaintenanceThemedControl
{
    void ApplyPalette(MaintenanceThemePalette palette);
}

internal sealed class MaintenanceButton : Button, IMaintenanceThemedControl
{
    private MaintenanceThemePalette _palette;
    private bool _hot;
    private bool _pressed;

    public MaintenanceButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(
            MaintenanceLayoutMetrics.ButtonMinimumWidth,
            MaintenanceLayoutMetrics.ButtonHeight);
        Padding = new Padding(18, 7, 18, 7);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MaintenanceButtonStyle VisualStyle { get; set; }

    public void ApplyPalette(MaintenanceThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Window;
        ForeColor = palette.Text;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hot = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = mevent.Button == MouseButtons.Left;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(ResolveCanvasColor());
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        bounds.Width--;
        bounds.Height--;

        Color background = ResolveBackground();
        Color foreground = Enabled
            ? _palette.IsHighContrast && (_hot || _pressed)
                ? _palette.AccentText
                : VisualStyle == MaintenanceButtonStyle.Danger && !_palette.IsHighContrast
                    ? Color.White
                : VisualStyle == MaintenanceButtonStyle.Accent
                    ? _palette.AccentText
                    : _palette.Text
            : _palette.DisabledText;
        Color border = _palette.IsHighContrast && Focused
            ? SystemColors.Highlight
            : _palette.Border;

        using GraphicsPath path = MaintenanceDrawing.CreateRoundedRectangle(
            bounds,
            _palette.IsHighContrast
                ? 0
                : MaintenanceDpi.Scale(MaintenanceLayoutMetrics.CornerRadius, DeviceDpi));
        using SolidBrush brush = new(background);
        pevent.Graphics.FillPath(brush, path);
        using Pen pen = new(border, MaintenanceDpi.Scale(1F, DeviceDpi));
        if (_palette.IsHighContrast) pevent.Graphics.DrawPath(pen, path);

        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            bounds,
            foreground,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            int focusInset = MaintenanceDpi.Scale(4, DeviceDpi);
            Rectangle focus = Rectangle.Inflate(bounds, -focusInset, -focusInset);
            ControlPaint.DrawFocusRectangle(pevent.Graphics, focus, foreground, background);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    private Color ResolveCanvasColor()
    {
        Color parent = Parent?.BackColor ?? _palette.Window;
        return parent.A == byte.MaxValue ? parent : _palette.Window;
    }

    private Color ResolveBackground()
    {
        if (!Enabled)
        {
            return _palette.IsHighContrast ? SystemColors.Control : _palette.Surface;
        }

        Color baseColor = VisualStyle switch
        {
            MaintenanceButtonStyle.Accent => _palette.Accent,
            MaintenanceButtonStyle.Danger => Color.FromArgb(205, 45, 54),
            _ => _palette.Card,
        };
        if (_palette.IsHighContrast)
        {
            return _pressed || _hot ? SystemColors.Highlight : SystemColors.Control;
        }

        return _pressed
            ? ControlPaint.Dark(baseColor, 0.10F)
            : _hot
                ? ControlPaint.Light(baseColor, 0.05F)
                : baseColor;
    }
}

internal sealed class MaintenanceCheckBox : CheckBox, IMaintenanceThemedControl
{
    private const int LogicalBoxSize = 18;
    private const int LogicalTextGap = 10;
    private const int LogicalHorizontalPadding = 1;
    private const int LogicalVerticalPadding = 6;
    private MaintenanceThemePalette _palette;
    private bool _hot;

    public MaintenanceCheckBox()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        AutoSize = true;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    public void ApplyPalette(MaintenanceThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Window;
        ForeColor = palette.Text;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        TextFormatFlags flags = TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding;
        Size textSize = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            flags);
        int boxSize = MaintenanceDpi.Scale(LogicalBoxSize, DeviceDpi);
        int gap = MaintenanceDpi.Scale(LogicalTextGap, DeviceDpi);
        int horizontalPadding = MaintenanceDpi.Scale(LogicalHorizontalPadding, DeviceDpi);
        int verticalPadding = MaintenanceDpi.Scale(LogicalVerticalPadding, DeviceDpi);
        return new Size(
            horizontalPadding + boxSize + gap + textSize.Width + horizontalPadding,
            Math.Max(boxSize, textSize.Height) + verticalPadding);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hot = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        Color canvas = BackColor.A == byte.MaxValue ? BackColor : _palette.Card;
        pevent.Graphics.Clear(canvas);
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int boxSize = MaintenanceDpi.Scale(LogicalBoxSize, DeviceDpi);
        int textGap = MaintenanceDpi.Scale(LogicalTextGap, DeviceDpi);
        int boxLeft = MaintenanceDpi.Scale(LogicalHorizontalPadding, DeviceDpi);
        int boxTop = Math.Max(0, (ClientSize.Height - boxSize) / 2);
        Rectangle box = new(boxLeft, boxTop, boxSize, boxSize);
        Color border = Enabled ? _palette.Border : _palette.DisabledText;
        Color fill = Checked && Enabled
            ? _palette.Accent
            : _hot && Enabled && !_palette.IsHighContrast
                ? _palette.Card
                : _palette.Surface;

        using GraphicsPath path = MaintenanceDrawing.CreateRoundedRectangle(
            box,
            _palette.IsHighContrast ? 0 : MaintenanceDpi.Scale(4, DeviceDpi));
        using SolidBrush brush = new(fill);
        using Pen pen = new(border, MaintenanceDpi.Scale(1F, DeviceDpi));
        pevent.Graphics.FillPath(brush, path);
        if (_palette.IsHighContrast) pevent.Graphics.DrawPath(pen, path);

        if (Checked)
        {
            Color checkColor = Enabled ? _palette.AccentText : _palette.DisabledText;
            using Pen checkPen = new(checkColor, MaintenanceDpi.Scale(2F, DeviceDpi))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            pevent.Graphics.DrawLines(
                checkPen,
                new[]
                {
                    new Point(
                        box.Left + MaintenanceDpi.Scale(4, DeviceDpi),
                        box.Top + MaintenanceDpi.Scale(9, DeviceDpi)),
                    new Point(
                        box.Left + MaintenanceDpi.Scale(8, DeviceDpi),
                        box.Bottom - MaintenanceDpi.Scale(5, DeviceDpi)),
                    new Point(
                        box.Right - MaintenanceDpi.Scale(3, DeviceDpi),
                        box.Top + MaintenanceDpi.Scale(4, DeviceDpi)),
                });
        }

        int textLeft = box.Right + textGap;
        Rectangle textBounds = new(
            textLeft,
            0,
            Math.Max(0, ClientSize.Width - textLeft),
            ClientSize.Height);
        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            textBounds,
            Enabled ? _palette.Text : _palette.DisabledText,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding);

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(
                pevent.Graphics,
                Rectangle.Inflate(
                    textBounds,
                    -MaintenanceDpi.Scale(1, DeviceDpi),
                    -MaintenanceDpi.Scale(3, DeviceDpi)),
                _palette.Text,
                canvas);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Parent?.PerformLayout(this, nameof(PreferredSize));
        Invalidate();
    }
}

internal sealed class MaintenanceCardPanel : Panel, IMaintenanceThemedControl
{
    private MaintenanceThemePalette _palette;

    public MaintenanceCardPanel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        DoubleBuffered = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsError { get; set; }

    public void ApplyPalette(MaintenanceThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Window;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? _palette.Window);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        bounds.Width--;
        bounds.Height--;
        using GraphicsPath path = MaintenanceDrawing.CreateRoundedRectangle(
            bounds,
            _palette.IsHighContrast
                ? 0
                : MaintenanceDpi.Scale(MaintenanceLayoutMetrics.CornerRadius, DeviceDpi));
        using SolidBrush brush = new(_palette.Window);
        using Pen pen = new(
            IsError ? _palette.Error : _palette.Border,
            MaintenanceDpi.Scale(1F, DeviceDpi));
        e.Graphics.FillPath(brush, path);
        if (_palette.IsHighContrast) e.Graphics.DrawPath(pen, path);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }
}

internal sealed class MaintenanceCloseScheduler : IMaintenanceCloseScheduler
{
    public IDisposable Schedule(Control owner, TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(callback);

        System.Windows.Forms.Timer timer = new()
        {
            Interval = Math.Max(1, (int)Math.Ceiling(delay.TotalMilliseconds)),
        };
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            timer.Stop();
            if (handler is not null)
            {
                timer.Tick -= handler;
            }

            timer.Dispose();
            if (!owner.IsDisposed)
            {
                callback();
            }
        };
        timer.Tick += handler;
        timer.Start();
        return timer;
    }
}

internal static class MaintenanceDrawing
{
    internal static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = Math.Max(0, radius * 2);
        if (diameter == 0 || bounds.Width <= diameter || bounds.Height <= diameter)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        Rectangle arc = new(bounds.Location, new Size(diameter, diameter));
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
}

internal static class MaintenanceWindowChrome
{
    private const uint DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const uint DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    internal static void TryApply(nint handle, MaintenanceThemePalette palette)
    {
        FoxMouse.Presentation.InstallerChrome.Apply(handle, palette.IsDark, palette.Window, palette.Text);
        if (handle == nint.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        try
        {
            int dark = palette.IsDark && !palette.IsHighContrast ? 1 : 0;
            int result = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref dark,
                sizeof(int));
            if (result != 0)
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref dark,
                    sizeof(int));
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int preference = DwmwcpRound;
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaWindowCornerPreference,
                    ref preference,
                    sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (SEHException)
        {
        }
    }

    internal static Color GetAccentColor()
    {
        try
        {
            if (DwmGetColorizationColor(out uint color, out _) == 0)
            {
                Color accent = Color.FromArgb(
                    (int)((color >> 16) & 0xFF),
                    (int)((color >> 8) & 0xFF),
                    (int)(color & 0xFF));
                return accent;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (SEHException)
        {
        }

        return SystemColors.Highlight;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        nint window,
        uint attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetColorizationColor(
        out uint colorizationColor,
        [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);
}
