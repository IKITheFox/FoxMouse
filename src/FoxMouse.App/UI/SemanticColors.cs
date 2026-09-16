namespace FoxMouse.App.UI;

internal enum WindowsThemeKind
{
    Light,
    Dark,
    HighContrast,
}

internal enum ThemeRole
{
    Default,
    Card,
    SecondaryText,
    AccentText,
}

internal readonly record struct SemanticColors(
    WindowsThemeKind Kind,
    Color Window,
    Color Surface,
    Color Card,
    Color Field,
    Color Text,
    Color SecondaryText,
    Color Border,
    Color Accent,
    Color Selection,
    Color SelectionText)
{
    public bool IsDark => Kind == WindowsThemeKind.Dark;

    public bool IsHighContrast => Kind == WindowsThemeKind.HighContrast;

    public static SemanticColors Create(WindowsThemeKind kind) => kind switch
    {
        WindowsThemeKind.HighContrast => new SemanticColors(
            kind,
            SystemColors.Window,
            SystemColors.Control,
            SystemColors.Control,
            SystemColors.Window,
            SystemColors.ControlText,
            SystemColors.GrayText,
            SystemColors.WindowFrame,
            SystemColors.Highlight,
            SystemColors.Highlight,
            SystemColors.HighlightText),
        WindowsThemeKind.Dark => new SemanticColors(
            kind,
            Color.FromArgb(32, 32, 32),
            Color.FromArgb(32, 32, 32),
            Color.FromArgb(40, 40, 40),
            Color.FromArgb(45, 45, 45),
            Color.FromArgb(243, 243, 243),
            Color.FromArgb(184, 184, 184),
            Color.FromArgb(70, 70, 70),
            SystemColors.Highlight,
            Color.FromArgb(62, 62, 62),
            Color.FromArgb(243, 243, 243)),
        _ => new SemanticColors(
            WindowsThemeKind.Light,
            Color.FromArgb(249, 249, 249),
            Color.FromArgb(249, 249, 249),
            Color.White,
            Color.White,
            Color.FromArgb(27, 27, 27),
            Color.FromArgb(96, 96, 96),
            Color.FromArgb(218, 218, 218),
            SystemColors.Highlight,
            Color.FromArgb(232, 232, 232),
            Color.FromArgb(27, 27, 27)),
    };
}
