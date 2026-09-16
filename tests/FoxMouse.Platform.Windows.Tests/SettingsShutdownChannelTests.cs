using FoxMouse.Platform.Windows.Configuration;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class SettingsShutdownChannelTests
{
    [Fact]
    public async Task ReopenedSettingsWindowReceivesANewExitRequest()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        int calls = 0;
        Task listener = SettingsShutdownChannel.ListenAsync(path, () =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, timeout.Token);
        try
        {
            await SettingsShutdownChannel.RequestAsync(path, timeout.Token);
            await SettingsShutdownChannel.RequestAsync(path, timeout.Token);
            Assert.Equal(2, calls);
        }
        finally { await timeout.CancelAsync(); await listener; }
    }

    [Fact]
    public async Task ExitWaitsForSettingsDecisionAndScopesBySettingsPath()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task listener = SettingsShutdownChannel.ListenAsync(path, () =>
        {
            requested.TrySetResult();
            return decision.Task;
        }, timeout.Token);
        try
        {
            await SettingsShutdownChannel.RequestAsync(path + ".other", timeout.Token);
            Assert.False(requested.Task.IsCompleted);
            Task exit = SettingsShutdownChannel.RequestAsync(path, timeout.Token);
            await requested.Task.WaitAsync(timeout.Token);
            Assert.False(exit.IsCompleted);
            decision.SetResult();
            await exit.WaitAsync(timeout.Token);
        }
        finally
        {
            decision.TrySetResult();
            await timeout.CancelAsync();
            await listener;
        }
    }
}
