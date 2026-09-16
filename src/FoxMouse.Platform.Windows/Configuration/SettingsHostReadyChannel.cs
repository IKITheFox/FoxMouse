using System.IO.Pipes;
using System.Text;

namespace FoxMouse.Platform.Windows.Configuration;

/// <summary>
/// Provides a short-lived, same-user acknowledgement channel so the tray host
/// can distinguish a created settings process from a settings window that is
/// actually ready for interaction.
/// </summary>
public static class SettingsHostReadyChannel
{
    public const string ArgumentName = "--launch-token";
    private const string PipePrefix = "FoxMouse.Settings.Ready.v1.";
    private const string ReadyMessage = "ready";

    public static string CreateToken() => Guid.NewGuid().ToString("N");

    public static bool IsValidToken(string? token) =>
        token is { Length: 32 } &&
        Guid.TryParseExact(token, "N", out _);

    public static async Task<bool> WaitForReadyAsync(
        string token,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using CancellationTokenSource timeoutCancellation = new(timeout);
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        try
        {
            await using NamedPipeServerStream server = new(
                PipePrefix + token,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(linkedCancellation.Token).ConfigureAwait(false);
            using StreamReader reader = new(
                server,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 64,
                leaveOpen: true);
            string? response = await reader.ReadLineAsync(linkedCancellation.Token).ConfigureAwait(false);
            return string.Equals(response, ReadyMessage, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    public static async Task<bool> SignalReadyAsync(
        string? token,
        CancellationToken cancellationToken = default,
        int connectTimeoutMilliseconds = 750)
    {
        if (!IsValidToken(token))
        {
            return false;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connectTimeoutMilliseconds);
        try
        {
            await using NamedPipeClientStream client = new(
                ".",
                PipePrefix + token,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            await using StreamWriter writer = new(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            await writer.WriteLineAsync(ReadyMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    private static void ValidateToken(string token)
    {
        if (!IsValidToken(token))
        {
            throw new ArgumentException("The settings launch token is invalid.", nameof(token));
        }
    }
}
