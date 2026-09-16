using System.Threading.Channels;

namespace FoxMouse.Platform.Windows.Diagnostics;

public sealed class RollingFileLogger : IAsyncDisposable
{
    private readonly string _directory;
    private readonly long _maximumFileBytes;
    private readonly int _maximumFiles;
    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerTask;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    public RollingFileLogger(string directory, long maximumFileBytes = 2 * 1024 * 1024, int maximumFiles = 5)
    {
        _directory = directory;
        _maximumFileBytes = Math.Clamp(maximumFileBytes, 64 * 1024, 32 * 1024 * 1024);
        _maximumFiles = Math.Clamp(maximumFiles, 1, 20);
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2_048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public void Information(string eventName, string? detail = null) => Write("INFO", eventName, detail);

    public void Warning(string eventName, string? detail = null) => Write("WARN", eventName, detail);

    public void Error(string eventName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // Exception messages frequently contain local paths, command lines or
        // other user data. Diagnostics retain only the failure class and code.
        string detail = exception is System.ComponentModel.Win32Exception win32
            ? $"{exception.GetType().Name}; nativeError={win32.NativeErrorCode}"
            : $"{exception.GetType().Name}; hresult=0x{exception.HResult:X8}";
        Write("ERROR", eventName, detail);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private void Write(string level, string eventName, string? detail)
    {
        string safeEvent = Sanitize(eventName, 96);
        string safeDetail = Sanitize(detail ?? string.Empty, 512);
        string line = $"{DateTimeOffset.UtcNow:O}\t{level}\t{safeEvent}\t{safeDetail}";
        _ = _channel.Writer.TryWrite(line);
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "FoxMouse.log");
            await foreach (string line in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                RotateIfNeeded(path);
                await File.AppendAllTextAsync(path, line + Environment.NewLine, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Diagnostics must never take down the application. Stop accepting
            // entries and degrade to a no-op logger after any writer failure.
            _channel.Writer.TryComplete();
        }
    }

    private async Task DisposeCoreAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // WriterLoopAsync already contains its failures, but Dispose must
            // remain safe even if a future writer implementation faults.
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    private void RotateIfNeeded(string currentPath)
    {
        FileInfo info = new(currentPath);
        if (!info.Exists || info.Length < _maximumFileBytes)
        {
            return;
        }

        string oldest = $"{currentPath}.{_maximumFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = _maximumFiles - 1; index >= 1; index--)
        {
            string source = $"{currentPath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{currentPath}.{index + 1}");
            }
        }

        File.Move(currentPath, $"{currentPath}.1");
    }

    private static string Sanitize(string value, int maximumLength)
    {
        string flattened = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return flattened.Length <= maximumLength ? flattened : flattened[..maximumLength];
    }
}
