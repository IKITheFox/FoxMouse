using FoxMouse.Settings.Services;

namespace FoxMouse.Settings.Tests;

public sealed class ProcessExclusionNameTests
{
    [Theory]
    [InlineData("game.exe", "game.exe")]
    [InlineData("  GAME.EXE  ", "GAME.EXE")]
    [InlineData(@"C:\Games\Example\play.exe", "play.exe")]
    [InlineData("\"C:\\Program Files\\Example\\tool.exe\"", "tool.exe")]
    public void TryNormalizeKeepsOnlyExecutableBaseName(string candidate, string expected)
    {
        Assert.True(ProcessExclusionName.TryNormalize(candidate, out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("game")]
    [InlineData("notes.txt")]
    public void TryNormalizeRejectsInvalidEntries(string? candidate)
    {
        Assert.False(ProcessExclusionName.TryNormalize(candidate, out string actual));
        Assert.Empty(actual);
    }
}
