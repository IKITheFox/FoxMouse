using System.ComponentModel;
using System.Diagnostics;
using FoxMouse.Platform.Windows.Configuration;
using FoxMouse.Platform.Windows.Diagnostics;

namespace FoxMouse.App;

internal sealed class SettingsHostBridge : IAsyncDisposable
{
    private const string SettingsExecutableName = "FoxMouse.Settings.exe";
    private static readonly TimeSpan LaunchReadyTimeout = TimeSpan.FromSeconds(4);

    private readonly SettingsStore _settingsStore;
    private readonly RollingFileLogger _logger;
    private readonly SynchronizationContext _uiContext;
    private readonly Action _reloadRequested;
    private readonly Action _previewRequested;
    private readonly Func<string?> _settingsExecutableResolver;
    private readonly TimeSpan _launchReadyTimeout;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listenerTask;
    private int _disposed;

    public SettingsHostBridge(
        SettingsStore settingsStore,
        RollingFileLogger logger,
        SynchronizationContext uiContext,
        Action reloadRequested,
        Action previewRequested,
        bool listenForChanges = true,
        Func<string?>? settingsExecutableResolver = null,
        TimeSpan? launchReadyTimeout = null)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        _uiContext = uiContext;
        _reloadRequested = reloadRequested;
        _previewRequested = previewRequested;
        _settingsExecutableResolver = settingsExecutableResolver ?? FindSettingsExecutable;
        _launchReadyTimeout = launchReadyTimeout ?? LaunchReadyTimeout;
        _listenerTask = listenForChanges ? ListenAsync() : Task.CompletedTask;
    }

    public async Task<bool> TryOpenAsync(
        string page,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        string? executable = _settingsExecutableResolver();
        if (executable is null)
        {
            return false;
        }

        string normalizedPage = page switch
        {
            "about" => "about",
            "exclusions" => "exclusions",
            _ => "general",
        };

        using CancellationTokenSource launchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        string launchToken = SettingsHostReadyChannel.CreateToken();
        Task<bool> readyTask = SettingsHostReadyChannel.WaitForReadyAsync(
            launchToken,
            _launchReadyTimeout,
            launchCancellation.Token);

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add("--page");
            startInfo.ArgumentList.Add(normalizedPage);
            startInfo.ArgumentList.Add(SettingsHostReadyChannel.ArgumentName);
            startInfo.ArgumentList.Add(launchToken);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                await launchCancellation.CancelAsync().ConfigureAwait(false);
                _ = await readyTask.ConfigureAwait(false);
                return false;
            }

            bool ready = await readyTask.ConfigureAwait(false);
            if (ready)
            {
                _logger.Information("settings-host-opened", $"page={normalizedPage}");
                return true;
            }

            if (!cancellationToken.IsCancellationRequested &&
                !_cancellation.IsCancellationRequested &&
                Volatile.Read(ref _disposed) == 0)
            {
                string state = TryHasExited(process) ? "exited" : "not-ready";
                _logger.Warning("settings-host-not-ready", $"page={normalizedPage}; state={state}");
            }

            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            await launchCancellation.CancelAsync().ConfigureAwait(false);
            _ = await readyTask.ConfigureAwait(false);
            _logger.Error("settings-host-launch-failed", exception);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _listenerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            await SettingsChangeChannel.ListenAsync(
                HandleMessageAsync,
                _cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("settings-change-listener-failed", exception);
        }
    }

    private Task HandleMessageAsync(SettingsChangeMessage message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            Volatile.Read(ref _disposed) != 0 ||
            !PathsEqual(message.SettingsPath, _settingsStore.SettingsPath))
        {
            return Task.CompletedTask;
        }

        try
        {
            _uiContext.Post(state =>
            {
                SettingsHostBridge owner = (SettingsHostBridge)state!;
                if (Volatile.Read(ref owner._disposed) == 0)
                {
                    if (message.Command == SettingsChangeMessage.PreviewCommand)
                    {
                        owner._previewRequested();
                    }
                    else
                    {
                        owner._reloadRequested();
                    }
                }
            }, this);
        }
        catch (Exception exception)
        {
            _logger.Error("settings-change-dispatch-failed", exception);
        }

        return Task.CompletedTask;
    }

    private static string? FindSettingsExecutable()
    {
        string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (string candidate in new[]
        {
            Path.Combine(baseDirectory, "Settings", SettingsExecutableName),
            Path.Combine(baseDirectory, SettingsExecutableName),
        })
        {
            string fullCandidate = Path.GetFullPath(candidate);
            string relative = Path.GetRelativePath(baseDirectory, fullCandidate);
            if (!relative.StartsWith("..", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative) &&
                File.Exists(fullCandidate))
            {
                return fullCandidate;
            }
        }

        return null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
