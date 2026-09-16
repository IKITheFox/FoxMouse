using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class ProductReleaseTests
{
    [Theory]
    [InlineData("1.0.0-beta", "1.0.0 Beta")]
    [InlineData("1.0.0-beta+abc123", "1.0.0 Beta")]
    [InlineData("1.0.0", "1.0.0")]
    public void FormatsDisplayWithoutBuildMetadata(string input, string expected) =>
        Assert.Equal(expected, ProductRelease.FormatInformationalVersion(input));

    [Fact]
    public void KeepsOlderInstalledVersionUnchanged() =>
        Assert.Equal("0.5.3", ProductRelease.DisplayInstalledVersion("0.5.3"));

    [Fact]
    public void CurrentReleaseIsLabeledBeta() =>
        Assert.Equal("1.0.0 Beta", ProductRelease.DisplayInstalledVersion("1.0.0"));
}
