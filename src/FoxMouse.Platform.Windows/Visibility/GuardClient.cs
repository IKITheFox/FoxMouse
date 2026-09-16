using System.Diagnostics;
using System.IO.Pipes;

namespace FoxMouse.Platform.Windows.Visibility;

public sealed class GuardClient : IAsyncDisposable
{
    private const int KnownShown = 0;
    private const int HidePendingOrHidden = 1;

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeTaskGate = new();
    private readonly object _fallbackGate = new();
    private readonly ISystemCursorController _fallback;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _process;
    private int _connectionPoisoned;
    private int _disposeStarted;
    private int _visibilityState = KnownShown;
    private int _guardLostSignaled;
    private long _generation;
    private Task? _disposeTask;

    public GuardClient()
        : this(new MagnificationCursorController())
    {
    }

    internal GuardClient(ISystemCursorController fallback) =>
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _connectionPoisoned) != 0)
            {
                return false;
            }

            try
            {
                return _pipe?.IsConnected == true && _process?.HasExited == false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    // Unknown is deliberately reported as hidden: after a hide request leaves
    // this process, only an in-order Show acknowledgement, or a local Show
    // after the Guard is confirmed dead, may clear the state.
    public bool IsHidden => Volatile.Read(ref _visibilityState) != KnownShown;

    public event EventHandler? GuardLost;

    public Task<bool> StartAsync(string guardExecutable, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        return ExecuteSerializedAsync(
            () => StartCoreAsync(guardExecutable, cancellationToken),
            cancellationToken);
    }

    private async Task<bool> StartCoreAsync(string guardExecutable, CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return true;
        }

        if (Volatile.Read(ref _connectionPoisoned) != 0)
        {
            return false;
        }

        string pipeName = $"FoxMouse.Guard.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        ProcessStartInfo startInfo = new()
        {
            FileName = guardExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--owner-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        _process = Process.Start(startInfo);
        if (_process is null)
        {
            PoisonConnection(signalGuardLost: false);
            return false;
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += HandleGuardExited;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            _reader = new StreamReader(_pipe, leaveOpen: true);
            _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            string? readyLine = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            GuardResponse? ready = readyLine is null ? null : GuardProtocol.ParseResponse(readyLine);
            bool readySucceeded = ready is
            {
                Operation: "ready",
                Success: true,
                Generation: 0,
                ProtocolVersion: GuardProtocol.Version,
            };
            if (readySucceeded)
            {
                Interlocked.Exchange(ref _guardLostSignaled, 0);
                return true;
            }

            PoisonConnection(signalGuardLost: false);
            return false;
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or
            IOException or
            System.Text.Json.JsonException or
            ObjectDisposedException or
            InvalidOperationException)
        {
            PoisonConnection(signalGuardLost: false);
            return false;
        }
    }

    public Task<bool> HideAsync(long generation, int leaseMilliseconds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        return ExecuteSerializedAsync(
            () => HideCoreAsync(generation, leaseMilliseconds, cancellationToken),
            cancellationToken);
    }

    private async Task<bool> HideCoreAsync(
        long generation,
        int leaseMilliseconds,
        CancellationToken cancellationToken)
    {
        // Mark Unknown before emitting the request. A lost acknowledgement can
        // mean the Guard will still execute the hide later.
        Interlocked.Exchange(ref _visibilityState, HidePendingOrHidden);
        Interlocked.Exchange(ref _generation, generation);
        GuardResponse? response = await ExchangeAsync(
            new GuardRequest("hide", generation, leaseMilliseconds),
            cancellationToken).ConfigureAwait(false);
        if (response is { Success: true })
        {
            return true;
        }

        if (response is not null)
        {
            // A negative hide acknowledgement is ordered, but an explicit
            // ordered Show acknowledgement is still required to clear Unknown.
            _ = await ShowCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _ = await RecoverPoisonedAsync(cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public Task<bool> RenewAsync(int leaseMilliseconds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        return ExecuteSerializedAsync(
            () => RenewCoreAsync(leaseMilliseconds, cancellationToken),
            cancellationToken);
    }

    private async Task<bool> RenewCoreAsync(int leaseMilliseconds, CancellationToken cancellationToken)
    {
        if (!IsHidden)
        {
            return true;
        }

        long generation = Interlocked.Read(ref _generation);
        GuardResponse? response = await ExchangeAsync(
            new GuardRequest("renew", generation, leaseMilliseconds),
            cancellationToken).ConfigureAwait(false);
        if (response is { Success: true })
        {
            return true;
        }

        // A failed renewal normally means the lease already restored the
        // cursor. Confirm that fact through the same ordered stream. A broken
        // stream remains Unknown until its Guard is confirmed dead.
        if (response is not null)
        {
            _ = await ShowCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _ = await RecoverPoisonedAsync(cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public Task<bool> ShowAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        return ExecuteSerializedAsync(
            () => ShowCoreAsync(cancellationToken),
            cancellationToken);
    }

    private async Task<bool> ShowCoreAsync(CancellationToken cancellationToken)
    {
        if (!IsHidden)
        {
            return true;
        }

        if (Volatile.Read(ref _connectionPoisoned) != 0 || !IsConnected)
        {
            PoisonConnection(signalGuardLost: true);
            return await RecoverPoisonedAsync(cancellationToken).ConfigureAwait(false);
        }

        long generation = Interlocked.Read(ref _generation);
        GuardResponse? response = await ExchangeAsync(
            new GuardRequest("show", generation),
            cancellationToken).ConfigureAwait(false);
        if (response is { Success: true })
        {
            Interlocked.Exchange(ref _visibilityState, KnownShown);
            return true;
        }

        if (response is null)
        {
            return await RecoverPoisonedAsync(cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public ValueTask DisposeAsync()
    {
        Task completion;
        TaskCompletionSource? completionSource = null;
        lock (_disposeTaskGate)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposeStarted, 1);
                completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completionSource.Task;
            }

            completion = _disposeTask;
        }

        // Publish the shared completion task before DisposeCoreAsync can run
        // synchronously or a GuardLost consumer can re-enter DisposeAsync.
        if (completionSource is not null)
        {
            _ = CompleteDisposeAsync(completionSource);
        }

        return new ValueTask(completion);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completionSource)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completionSource.TrySetResult();
        }
        catch (Exception exception)
        {
            completionSource.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Never complete disposal while the cursor is Unknown/hidden. If
            // IPC is poisoned, wait for the Guard to become incapable of a
            // late hide before invoking the local idempotent recovery path.
            while (IsHidden)
            {
                _ = await ShowCoreAsync(CancellationToken.None).ConfigureAwait(false);
                if (IsHidden)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }

            if (IsConnected)
            {
                _ = await ExchangeAsync(
                    new GuardRequest("exit", Interlocked.Read(ref _generation)),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeIpc();
            if (_process is not null)
            {
                _process.Exited -= HandleGuardExited;
                _process.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }

        lock (_fallbackGate)
        {
            _fallback.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private async Task<bool> ExecuteSerializedAsync(
        Func<Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An operation may have passed its public pre-check immediately
            // before disposal took ownership of the gate.
            ThrowIfDisposing();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<GuardResponse?> ExchangeAsync(GuardRequest request, CancellationToken cancellationToken)
    {
        if (!IsConnected || _reader is null || _writer is null)
        {
            PoisonConnection(signalGuardLost: IsHidden);
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
        bool entered = false;
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (!IsConnected || _reader is null || _writer is null)
            {
                PoisonConnection(signalGuardLost: IsHidden);
                return null;
            }

            await _writer.WriteLineAsync(GuardProtocol.SerializeRequest(request).AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            string? line = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            GuardResponse? response = line is null ? null : GuardProtocol.ParseResponse(line);
            bool matches = response is not null &&
                           response.ProtocolVersion == GuardProtocol.Version &&
                           string.Equals(response.Operation, request.Operation, StringComparison.Ordinal) &&
                           response.Generation == request.Generation;
            if (!matches)
            {
                PoisonConnection(signalGuardLost: IsHidden);
                return null;
            }

            return response;
        }
        catch (Exception exception) when (exception is
            IOException or
            OperationCanceledException or
            System.Text.Json.JsonException or
            ObjectDisposedException or
            InvalidOperationException)
        {
            PoisonConnection(signalGuardLost: IsHidden);
            return null;
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    private void HandleGuardExited(object? sender, EventArgs e)
    {
        // An exited Guard can no longer execute a delayed hide. It is now safe
        // for this process to issue the independent idempotent Show fallback.
        Interlocked.Exchange(ref _connectionPoisoned, 1);
        try
        {
            DisposeIpc();
        }
        finally
        {
            try
            {
                if (IsHidden && HasGuardExited())
                {
                    RestoreFallbackAfterGuardExit();
                }
            }
            finally
            {
                QueueGuardLost();
            }
        }
    }

    private async Task<bool> RecoverPoisonedAsync(CancellationToken cancellationToken)
    {
        if (!IsHidden)
        {
            return true;
        }

        Process? process = _process;
        if (process is null)
        {
            return RestoreFallbackAfterGuardExit();
        }

        try
        {
            if (!process.HasExited)
            {
                using CancellationTokenSource timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return HasGuardExited() && RestoreFallbackAfterGuardExit();
    }

    private bool HasGuardExited()
    {
        try
        {
            return _process is null || _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void PoisonConnection(bool signalGuardLost)
    {
        Interlocked.Exchange(ref _connectionPoisoned, 1);
        DisposeIpc();
        if (signalGuardLost)
        {
            QueueGuardLost();
        }
    }

    private void DisposeIpc()
    {
        DisposeNoThrow(_writer);
        DisposeNoThrow(_reader);
        DisposeNoThrow(_pipe);
    }

    internal static void DisposeNoThrow(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception)
        {
            // Cleanup is deliberately no-throw. StreamWriter.Dispose can
            // report InvalidOperationException while an async write is being
            // interrupted by a Guard exit.
        }
    }

    private void QueueGuardLost()
    {
        if (Interlocked.Exchange(ref _guardLostSignaled, 1) != 0)
        {
            return;
        }

        _ = ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.InvokeGuardLost(),
            this,
            preferLocal: false);
    }

    private void InvokeGuardLost()
    {
        try
        {
            GuardLost?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A consumer callback must never suppress cursor recovery.
        }
    }

    private bool RestoreFallbackAfterGuardExit()
    {
        bool restored;
        lock (_fallbackGate)
        {
            try
            {
                restored = _fallback.TrySetVisible(true);
            }
            catch (ObjectDisposedException)
            {
                restored = false;
            }
            catch (Exception)
            {
                // An exited Guard cannot perform a late hide. Keep Unknown if
                // the independent native recovery API itself fails; callers
                // and disposal will retry instead of asserting visibility.
                restored = false;
            }
        }

        if (restored)
        {
            Interlocked.Exchange(ref _visibilityState, KnownShown);
        }

        return restored;
    }

    private void ThrowIfDisposing() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
}
