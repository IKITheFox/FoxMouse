using FoxMouse.Platform.Windows.Configuration;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class SettingsHostReadyChannelTests
{
    [Fact]
    public void TokensAreOpaqueAndStrictlyValidated()
    {
        string token = SettingsHostReadyChannel.CreateToken();

        Assert.True(SettingsHostReadyChannel.IsValidToken(token));
        Assert.False(SettingsHostReadyChannel.IsValidToken(null));
        Assert.False(SettingsHostReadyChannel.IsValidToken("../not-a-pipe-token"));
        Assert.False(SettingsHostReadyChannel.IsValidToken(Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public async Task SameUserClientAcknowledgesAReadyWindow()
    {
        string token = SettingsHostReadyChannel.CreateToken();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        Task<bool> waiting = SettingsHostReadyChannel.WaitForReadyAsync(
            token,
            TimeSpan.FromSeconds(5),
            cancellation.Token);
        bool signalled = await SettingsHostReadyChannel.SignalReadyAsync(
            token,
            cancellation.Token,
            connectTimeoutMilliseconds: 2_000);

        Assert.True(signalled);
        Assert.True(await waiting);
    }

    [Fact]
    public async Task MissingAcknowledgementTimesOutWithoutThrowing()
    {
        bool ready = await SettingsHostReadyChannel.WaitForReadyAsync(
            SettingsHostReadyChannel.CreateToken(),
            TimeSpan.FromMilliseconds(75));

        Assert.False(ready);
    }

    [Fact]
    public async Task InvalidSignalTokenIsIgnored()
    {
        Assert.False(await SettingsHostReadyChannel.SignalReadyAsync("invalid"));
    }
}
