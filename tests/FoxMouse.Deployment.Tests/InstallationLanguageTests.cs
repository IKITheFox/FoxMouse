using FoxMouse.Deployment;

namespace FoxMouse.Deployment.Tests;

public sealed class InstallationLanguageTests
{
    [Theory]
    [InlineData("{\"language\":\"en-US\"}", "en-US")]
    [InlineData("{\"language\":\"zh-CN\"}", "zh-CN")]
    [InlineData("{\"language\":\"system\"}", "system")]
    [InlineData("{\"language\":42}", "system")]
    [InlineData("{\"language\":\"unknown\"}", "system")]
    [InlineData("{}", "system")]
    [InlineData("[]", "system")]
    [InlineData("broken", "system")]
    public void ReadsPreferenceWithoutChangingConfiguration(string json, string expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "FoxMouse-LanguageTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, json);
            Assert.Equal(expected, InstallationLanguage.ReadPreference(root));
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingPreferenceDoesNotCreateConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), "FoxMouse-LanguageTest-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("system", InstallationLanguage.ReadPreference(root));
        Assert.False(Directory.Exists(root));
    }
}
