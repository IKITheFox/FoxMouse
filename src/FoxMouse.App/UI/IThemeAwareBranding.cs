namespace FoxMouse.App.UI;

/// <summary>
/// Implemented by long-lived WinForms surfaces that show the FoxMouse mark.
/// The application host calls this after a live Windows theme transition.
/// </summary>
internal interface IThemeAwareBranding
{
    void RefreshBranding(SemanticColors colors);
}
