using Microsoft.Win32;

namespace FoxMouse.App.UI;

internal sealed class WindowsThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    public WindowsThemeService()
    {
        Colors = SemanticColors.Create(DetectTheme());
    }

    public SemanticColors Colors { get; private set; }

    public void Refresh()
    {
        Colors = SemanticColors.Create(DetectTheme());
    }

    public void ApplyTo(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        ApplyControl(form, Colors.Window);
        if (form.IsHandleCreated)
        {
            DwmWindowTheme.TryApply(form.Handle, Colors);
            form.Invalidate(invalidateChildren: true);
        }
        else
        {
            form.HandleCreated -= HandleFormCreated;
            form.HandleCreated += HandleFormCreated;
        }
    }

    public void ApplyTo(ContextMenuStrip menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        menu.BackColor = Colors.Surface;
        menu.ForeColor = Colors.Text;
        menu.Renderer = Colors.IsHighContrast
            ? new ToolStripSystemRenderer()
            : new ThemedToolStripRenderer(Colors);

        foreach (ToolStripItem item in menu.Items)
        {
            item.BackColor = Colors.Surface;
            item.ForeColor = item.Enabled ? Colors.Text : Colors.SecondaryText;
        }

        menu.Invalidate();
    }

    private void HandleFormCreated(object? sender, EventArgs e)
    {
        if (sender is not Form form)
        {
            return;
        }

        form.HandleCreated -= HandleFormCreated;
        DwmWindowTheme.TryApply(form.Handle, Colors);
    }

    private void ApplyControl(Control control, Color inheritedSurface)
    {
        ThemeRole role = control.Tag is ThemeRole semanticRole
            ? semanticRole
            : ThemeRole.Default;
        if (role == ThemeRole.Default
            && control is Label
            && control.ForeColor == SystemColors.GrayText)
        {
            role = ThemeRole.SecondaryText;
            control.Tag = role;
        }

        control.ForeColor = role switch
        {
            ThemeRole.SecondaryText => Colors.SecondaryText,
            ThemeRole.AccentText => Colors.Accent,
            _ => Colors.Text,
        };

        if (Colors.IsHighContrast
            && control is TextBoxBase or ListBox or ComboBox or NumericUpDown)
        {
            control.ForeColor = SystemColors.WindowText;
        }

        Color surface = role switch
        {
            ThemeRole.Card => Colors.Card,
            _ when control is TextBoxBase or ListBox or ComboBox or NumericUpDown => Colors.Field,
            _ when control is Form => Colors.Window,
            _ => inheritedSurface,
        };
        control.BackColor = surface;

        if (control is ButtonBase button)
        {
            button.UseVisualStyleBackColor = !Colors.IsHighContrast;
        }

        if (control is LinkLabel linkLabel)
        {
            linkLabel.LinkColor = Colors.Accent;
            linkLabel.ActiveLinkColor = Colors.Selection;
            linkLabel.VisitedLinkColor = Colors.Accent;
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, surface);
        }
    }

    private static WindowsThemeKind DetectTheme()
    {
        if (SystemInformation.HighContrast)
        {
            return WindowsThemeKind.HighContrast;
        }

        try
        {
            object? value = Registry.GetValue(
                $@"HKEY_CURRENT_USER\{PersonalizeKey}",
                AppsUseLightTheme,
                defaultValue: 1);
            if (value is int lightThemeValue)
            {
                return lightThemeValue == 0
                    ? WindowsThemeKind.Dark
                    : WindowsThemeKind.Light;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (System.Security.SecurityException)
        {
        }

        return SystemColors.Window.GetBrightness() < 0.5F
            ? WindowsThemeKind.Dark
            : WindowsThemeKind.Light;
    }
}
