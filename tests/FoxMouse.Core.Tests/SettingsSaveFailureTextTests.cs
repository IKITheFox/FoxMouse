namespace FoxMouse.Core.Tests;

public sealed class SettingsSaveFailureTextTests
{
    [Theory]
    [InlineData("en-US", "Unable to save settings.")]
    [InlineData("zh-CN", "无法保存设置。")]
    public void SaveFailureUsesSelectedLanguageWithoutLeakingOsMessage(string language, string expected)
    {
        IOException error = new("PRIVATE_PATH 中文操作系统消息", unchecked((int)0x80070050));
        string message = UiText.SettingsSaveFailure(error, language);
        Assert.StartsWith(expected, message);
        Assert.Contains("0x80070050", message);
        Assert.DoesNotContain("PRIVATE_PATH", message);
        Assert.DoesNotContain("中文操作系统消息", message);
    }
}
