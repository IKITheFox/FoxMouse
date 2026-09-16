using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using FoxMouse.Platform.Windows.Visibility;

namespace FoxMouse.Platform.Windows.Tests;

[Collection(GuardProcessCollection.Name)]
public sealed class GuardClientProcessTests
{
    private const string GuardPathVariable = "FOXMOUSE_TEST_GUARD_EXECUTABLE";
    private const string ExtraArgumentVariable = "FOXMOUSE_TEST_GUARD_EXTRA_ARGUMENT";

    [Fact]
    public async Task FakeGuardCompletesHideRenewShowRoundTrip()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        await using GuardClient client = CreateClient();

        Assert.True(await client.StartAsync(paths.Launcher));
        Assert.True(client.IsConnected);

        Assert.True(await client.HideAsync(generation: 41, leaseMilliseconds: 1_000));
        Assert.True(client.IsHidden);
        Assert.True(await client.RenewAsync(leaseMilliseconds: 1_000));
        Assert.True(client.IsHidden);
        Assert.True(await client.ShowAsync());
        Assert.False(client.IsHidden);
    }

    [Fact]
    public async Task FakeGuardProcessLossClearsHiddenStateAndRaisesGuardLost()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        await using GuardClient client = CreateClient();
        TaskCompletionSource guardLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GuardLost += (_, _) => guardLost.TrySetResult();

        Assert.True(await client.StartAsync(paths.Launcher));
        Assert.True(await client.HideAsync(generation: 73, leaseMilliseconds: 2_000));
        Assert.True(client.IsHidden);

        Process launcher = GetLauncherProcess(client);
        launcher.Kill(entireProcessTree: true);
        await guardLost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(client.IsConnected);
        Assert.False(client.IsHidden);
        Assert.True(await client.ShowAsync());
    }

    [Fact]
    public async Task GuardExitFallbackRestoresWithoutUsingRealCursorBackend()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        MagnificationCursorControllerTests.FakeMagnificationApi fallbackApi = new();
        await using GuardClient client = new(new MagnificationCursorController(fallbackApi));
        TaskCompletionSource guardLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GuardLost += (_, _) => guardLost.TrySetResult();

        Assert.True(await client.StartAsync(paths.Launcher));
        Assert.True(await client.HideAsync(generation: 79, leaseMilliseconds: 2_000));
        Assert.True(client.IsHidden);

        GetLauncherProcess(client).Kill(entireProcessTree: true);
        await guardLost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(client.IsHidden);
        Assert.Equal([true], fallbackApi.VisibilityTransitions);
    }

    [Fact]
    public async Task HideAcknowledgementLossIsTreatedAsUnknownAndRestored()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        using EnvironmentVariableScope fault = new(
            ExtraArgumentVariable,
            "--test-exit-after-hide-before-reply");
        await using GuardClient client = CreateClient();
        TaskCompletionSource guardLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GuardLost += (_, _) => guardLost.TrySetResult();

        Assert.True(await client.StartAsync(paths.Launcher));

        Assert.False(await client.HideAsync(generation: 91, leaseMilliseconds: 2_000));
        await guardLost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(client.IsHidden);
        Assert.True(await client.ShowAsync());
    }

    [Fact]
    public async Task TimedOutHideCannotBeClearedBeforeDelayedGuardExits()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        using EnvironmentVariableScope fault = new(
            ExtraArgumentVariable,
            "--test-delay-hide-then-exit-before-reply");
        await using GuardClient client = CreateClient();
        TaskCompletionSource guardLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GuardLost += (_, _) => guardLost.TrySetResult();

        Assert.True(await client.StartAsync(paths.Launcher));

        Task<bool> hide = client.HideAsync(generation: 103, leaseMilliseconds: 2_000);
        await guardLost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The request has timed out, but the Guard is deliberately still alive
        // and will execute the hide later. Unknown must not be cleared early.
        Assert.True(client.IsHidden);
        Assert.False(GetLauncherProcess(client).HasExited);

        Assert.False(await hide.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(client.IsConnected);
        Assert.False(client.IsHidden);
        Assert.True(await client.ShowAsync());
    }

    [Fact]
    public async Task TimedOutSuccessfulHideWaitsForGuardFinallyBeforeRecovery()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        using EnvironmentVariableScope fault = new(
            ExtraArgumentVariable,
            "--test-delay-hide-reply");
        await using GuardClient client = CreateClient();
        TaskCompletionSource guardLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GuardLost += (_, _) => guardLost.TrySetResult();

        Assert.True(await client.StartAsync(paths.Launcher));

        Assert.False(await client.HideAsync(generation: 109, leaseMilliseconds: 2_000));
        await guardLost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Process launcher = GetLauncherProcess(client);
        Assert.True(launcher.HasExited);
        Assert.Equal(6, launcher.ExitCode);
        Assert.False(client.IsConnected);
        Assert.False(client.IsHidden);
    }

    [Fact]
    public async Task InFlightShowIsLinearizedBeforeNewGenerationHide()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        using EnvironmentVariableScope fault = new(
            ExtraArgumentVariable,
            "--test-delay-show-reply");
        await using GuardClient client = CreateClient();

        Assert.True(await client.StartAsync(paths.Launcher));
        Assert.True(await client.HideAsync(generation: 211, leaseMilliseconds: 2_000));

        Task<bool> show = client.ShowAsync();
        await Task.Delay(75);
        Assert.False(show.IsCompleted);
        Task<bool> newerHide = client.HideAsync(generation: 212, leaseMilliseconds: 2_000);

        Assert.True(await show.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await newerHide.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(client.IsHidden);
        Assert.True(await client.ShowAsync());
        Assert.False(client.IsHidden);
    }

    [Fact]
    public async Task ConcurrentDisposeCallsShareRecoveryCompletion()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        using EnvironmentVariableScope fault = new(
            ExtraArgumentVariable,
            "--test-delay-hide-then-exit-before-reply");
        GuardClient client = CreateClient();
        try
        {
            TaskCompletionSource guardLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.GuardLost += (_, _) => guardLost.TrySetResult();
            Assert.True(await client.StartAsync(paths.Launcher));

            Task<bool> hide = client.HideAsync(generation: 313, leaseMilliseconds: 2_000);
            await guardLost.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task firstDispose = client.DisposeAsync().AsTask();
            Task secondDispose = client.DisposeAsync().AsTask();

            Assert.Same(firstDispose, secondDispose);
            Assert.False(firstDispose.IsCompleted);
            Assert.False(await hide.WaitAsync(TimeSpan.FromSeconds(10)));
            await firstDispose.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(client.IsHidden);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task GuardLostConsumerCanSynchronouslyRequestShowWithoutDeadlock()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        using EnvironmentVariableScope fault = new(
            ExtraArgumentVariable,
            "--test-delay-hide-then-exit-before-reply");
        await using GuardClient client = CreateClient();
        TaskCompletionSource<bool> callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GuardLost += (_, _) =>
        {
            try
            {
                callback.TrySetResult(client.ShowAsync().GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                callback.TrySetException(exception);
            }
        };

        Assert.True(await client.StartAsync(paths.Launcher));
        Assert.False(await client.HideAsync(generation: 419, leaseMilliseconds: 2_000));

        Assert.True(await callback.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(client.IsHidden);
    }

    [Fact]
    public void IpcCleanupSuppressesAsyncWriterInvalidOperation()
    {
        GuardClient.DisposeNoThrow(new InvalidOperationOnDispose());
    }

    [Fact]
    public async Task FakeGuardLeaseExpiresWhileIpcReadIsIdle()
    {
        BinaryPaths paths = GetBinaryPaths();
        Assert.True(File.Exists(paths.Guard), $"Guard executable was not built: {paths.Guard}");
        Assert.True(File.Exists(paths.Launcher), $"Fake Guard launcher was not built: {paths.Launcher}");
        using EnvironmentVariableScope guardPath = new(GuardPathVariable, paths.Guard);
        await using GuardClient client = CreateClient();

        Assert.True(await client.StartAsync(paths.Launcher));
        Assert.True(await client.HideAsync(generation: 117, leaseMilliseconds: 200));

        await Task.Delay(500);

        Assert.False(await client.RenewAsync(leaseMilliseconds: 200));
        Assert.True(await client.ShowAsync());
        Assert.False(client.IsHidden);
    }

    private static Process GetLauncherProcess(GuardClient client)
    {
        FieldInfo processField = typeof(GuardClient).GetField(
                                     "_process",
                                     BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException("GuardClient process field was not found.");
        return processField.GetValue(client) as Process
               ?? throw new InvalidOperationException("GuardClient did not retain its launcher process.");
    }

    private static GuardClient CreateClient() => new(new FakeSystemCursorController());

    private static BinaryPaths GetBinaryPaths([CallerFilePath] string sourcePath = "")
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string testProjectDirectory = System.IO.Path.GetDirectoryName(sourcePath)
                                      ?? throw new InvalidOperationException("Test project directory was not found.");
        const string targetFramework = "net10.0-windows10.0.17763.0";
        string guard = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            testProjectDirectory,
            "..",
            "..",
            "src",
            "FoxMouse.Guard",
            "bin",
            configuration,
            targetFramework,
            "win-x64",
            "FoxMouse.Guard.exe"));
        string launcher = System.IO.Path.Combine(
            testProjectDirectory,
            "FakeGuardLauncher",
            "bin",
            configuration,
            targetFramework,
            "win-x64",
            "FoxMouse.FakeGuardLauncher.exe");
        return new BinaryPaths(guard, launcher);
    }

    private sealed record BinaryPaths(string Guard, string Launcher);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
    }

    private sealed class InvalidOperationOnDispose : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("simulated async write in progress");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GuardProcessCollection
{
    public const string Name = "Guard process tests";
}
