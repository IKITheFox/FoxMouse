using FoxMouse.App.UI;
using FoxMouse.Core;

namespace FoxMouse.App;

internal sealed class AboutForm : Form, IThemeAwareBranding
{
    private readonly PictureBox _brandLogo;

    public AboutForm(WindowsThemeService themeService, Icon applicationIcon)
    {
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(applicationIcon);

        Text = UiText.Get("AboutMenu");
        ClientSize = new Size(464, 196);
        MinimumSize = new Size(464, 196);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = applicationIcon;
        AccessibleName = UiText.Get("AboutMenu");
        AccessibleDescription = UiText.Get("AboutHint");

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 20),
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        FlowLayoutPanel identity = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left,
        };
        _brandLogo = new PictureBox
        {
            Image = FoxMouseIconFactory.CreateBrandMark(themeService.Colors),
            Size = new Size(56, 56),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 16, 0),
            TabStop = false,
            AccessibleName = UiText.Get("Logo"),
        };
        TableLayoutPanel identityText = new()
        {
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 2, 0, 0),
        };
        Label productName = new()
        {
            AutoSize = true,
            Text = "FoxMouse",
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            Margin = Padding.Empty,
            AccessibleName = "FoxMouse",
        };
        Label version = new()
        {
            AutoSize = true,
            Text = UiText.Format("VersionLabel", FoxMouse.Core.ProductRelease.DisplayVersion),
            Tag = ThemeRole.SecondaryText,
            Margin = new Padding(0, 4, 0, 0),
            AccessibleName = UiText.Get("AboutHint"),
        };
        identityText.Controls.Add(productName, 0, 0);
        identityText.Controls.Add(version, 0, 1);
        identity.Controls.Add(_brandLogo);
        identity.Controls.Add(identityText);

        FlowLayoutPanel commands = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        Button close = new()
        {
            AutoSize = true,
            Text = UiText.Get("Close"),
            DialogResult = DialogResult.Cancel,
            Padding = new Padding(12, 2, 12, 2),
            AccessibleName = UiText.Get("Close"),
        };
        commands.Controls.Add(close);

        root.Controls.Add(identity, 0, 0);
        root.Controls.Add(commands, 0, 1);
        Controls.Add(root);

        AcceptButton = close;
        CancelButton = close;
        themeService.ApplyTo(this);
    }

    public void RefreshBranding(SemanticColors colors)
    {
        Image replacement = FoxMouseIconFactory.CreateBrandMark(colors);
        Image? previous = _brandLogo.Image;
        _brandLogo.Image = replacement;
        previous?.Dispose();
        _brandLogo.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeImages(Controls);
        }

        base.Dispose(disposing);
    }

    private static string NormalizeVersion(string version)
    {
        int metadataSeparator = version.IndexOf('+', StringComparison.Ordinal);
        return metadataSeparator >= 0 ? version[..metadataSeparator] : version;
    }

    private static void DisposeImages(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            if (control is PictureBox pictureBox)
            {
                Image? image = pictureBox.Image;
                pictureBox.Image = null;
                image?.Dispose();
            }

            if (control.HasChildren)
            {
                DisposeImages(control.Controls);
            }
        }
    }
}
