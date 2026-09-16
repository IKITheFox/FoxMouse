using FoxMouse.Settings;

namespace FoxMouse.Settings.Tests;

public sealed class SettingsClosePolicyTests
{
    [Theory]
    [InlineData(SettingsCloseChoice.Cancel, false, false, false)]
    [InlineData(SettingsCloseChoice.Cancel, true, false, true)]
    [InlineData(SettingsCloseChoice.Discard, false, false, true)]
    [InlineData(SettingsCloseChoice.Discard, true, false, true)]
    [InlineData(SettingsCloseChoice.Save, false, true, true)]
    [InlineData(SettingsCloseChoice.Save, true, true, true)]
    [InlineData(SettingsCloseChoice.Save, false, false, false)]
    [InlineData(SettingsCloseChoice.Save, true, false, false)]
    public void CloseDecisionPreservesUserDataAndGlobalExitSemantics(
        SettingsCloseChoice choice, bool globalExit, bool saved, bool expected) =>
        Assert.Equal(expected, SettingsClosePolicy.ShouldClose(choice, globalExit, saved));
}
