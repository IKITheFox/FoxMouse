using System.Globalization;

namespace FoxMouse.Core.Tests;

public sealed class UiLanguageTests
{
    [Fact]
    public void EnglishAndChineseResourcesAreAvailableWithoutChangingNumberCulture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        Assert.Equal("Save changes?", UiText.Get("SaveChanges", "en-US"));
        Assert.Equal("保存更改？", UiText.Get("SaveChanges", "zh-CN"));
        Assert.Contains("{0}", UiText.Get("PreviewError", "en-US"));
        Assert.Same(before, CultureInfo.CurrentCulture);
        Assert.Throws<System.Resources.MissingManifestResourceException>(() => UiText.Get("MissingKey", "en-US"));
    }
    [Theory]
    [InlineData("system", "zh-TW", "zh-CN")]
    [InlineData(null, "en-GB", "en-US")]
    [InlineData("invalid", "de-DE", "en-US")]
    [InlineData("en-US", "zh-CN", "en-US")]
    [InlineData("ZH-CN", "en-US", "zh-CN")]
    public void ResolvesExplicitAndSystemLanguages(string? preference, string system, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(preference, new CultureInfo(system)));
    }

    [Fact]
    public void OldSettingsKeepSystemDefaultAndInvalidValuesAreNormalized()
    {
        Assert.Equal("system", FoxMouseSettings.Default.Language);
        Assert.Equal("system", SettingsNormalizer.Normalize(new() { Language = "unknown" }).Language);
    }
}
