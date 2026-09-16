using FoxMouse.Platform.Windows.Configuration;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class SettingsChangeChannelTests
{
    [Fact]
    public void ParserAcceptsCurrentReloadMessageAndRejectsMalformedInput()
    {
        const string json = """
            {"protocolVersion":1,"command":"reload-settings","settingsPath":"C:\\Users\\test\\settings.json","revision":"abc123","sentAtUtc":"2026-09-04T08:00:00Z"}
            """;

        SettingsChangeMessage? parsed = SettingsChangeChannel.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(SettingsChangeMessage.ReloadSettingsCommand, parsed.Command);
        Assert.Equal("abc123", parsed.Revision);
        Assert.Null(SettingsChangeChannel.TryParse("not json"));
        Assert.Null(SettingsChangeChannel.TryParse("{\"protocolVersion\":2,\"command\":\"reload-settings\"}"));
        Assert.Null(SettingsChangeChannel.TryParse(new string('x', SettingsChangeChannel.MaximumMessageCharacters + 1)));
    }

    [Fact]
    public void ParserAcceptsCurrentPreviewMessage()
    {
        const string json = """
            {"protocolVersion":1,"command":"preview","settingsPath":"C:\\Users\\test\\settings.json","revision":"preview123","sentAtUtc":"2026-09-04T08:00:00Z"}
            """;

        SettingsChangeMessage? parsed = SettingsChangeChannel.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(SettingsChangeMessage.PreviewCommand, parsed.Command);
        Assert.Equal("preview123", parsed.Revision);
    }

    [Fact]
    public async Task SameUserPipeDeliversReloadNotification()
    {
        string pipeName = $"FoxMouse.SettingsChanged.Tests.{Guid.NewGuid():N}";
        string settingsPath = Path.Combine(Path.GetTempPath(), "FoxMouse", "settings.json");
        TaskCompletionSource<SettingsChangeMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        Task listener = SettingsChangeChannel.ListenAsync(
            (message, _) =>
            {
                received.TrySetResult(message);
                return Task.CompletedTask;
            },
            cancellation.Token,
            pipeName);

        bool delivered = await SettingsChangeChannel.NotifyReloadAsync(
            settingsPath,
            cancellation.Token,
            pipeName,
            connectTimeoutMilliseconds: 2_000);
        SettingsChangeMessage message = await received.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await listener;

        Assert.True(delivered);
        Assert.Equal(Path.GetFullPath(settingsPath), message.SettingsPath);
        Assert.Equal(SettingsChangeMessage.CurrentProtocolVersion, message.ProtocolVersion);
    }

    [Fact]
    public async Task SameUserPipeDeliversPreviewNotification()
    {
        string pipeName = $"FoxMouse.SettingsChanged.Tests.{Guid.NewGuid():N}";
        string settingsPath = Path.Combine(Path.GetTempPath(), "FoxMouse", "settings.json");
        TaskCompletionSource<SettingsChangeMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        Task listener = SettingsChangeChannel.ListenAsync(
            (message, _) =>
            {
                received.TrySetResult(message);
                return Task.CompletedTask;
            },
            cancellation.Token,
            pipeName);

        bool delivered = await SettingsChangeChannel.NotifyPreviewAsync(
            settingsPath,
            cancellation.Token,
            pipeName,
            connectTimeoutMilliseconds: 2_000);
        SettingsChangeMessage message = await received.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await listener;

        Assert.True(delivered);
        Assert.Equal(SettingsChangeMessage.PreviewCommand, message.Command);
        Assert.Equal(Path.GetFullPath(settingsPath), message.SettingsPath);
    }

    [Fact]
    public async Task MissingListenerIsReportedWithoutThrowing()
    {
        bool delivered = await SettingsChangeChannel.NotifyReloadAsync(
            Path.Combine(Path.GetTempPath(), "FoxMouse", "settings.json"),
            pipeName: $"FoxMouse.SettingsChanged.Tests.Missing.{Guid.NewGuid():N}",
            connectTimeoutMilliseconds: 50);

        Assert.False(delivered);
    }
}
