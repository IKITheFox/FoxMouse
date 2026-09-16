using System.IO.Pipes;
using System.Text;

namespace FoxMouse.Settings;

internal static class SettingsActivationChannel
{
    private const string PipeName = "FoxMouse.Settings.Activation.v1";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public static async Task<bool> NotifyAsync(
        SettingsActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using NamedPipeClientStream client = new(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
            await using StreamWriter writer = new(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            string payload = request.LaunchToken is null
                ? request.Page.ToString()
                : $"{request.Page}|{request.LaunchToken}";
            await writer.WriteLineAsync(payload.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static Task ListenAsync(
        Action<SettingsActivationRequest> onActivation,
        CancellationToken cancellationToken) =>
        ListenAsync(onActivation, cancellationToken, PipeName);

    internal static async Task ListenAsync(
        Action<SettingsActivationRequest> onActivation,
        CancellationToken cancellationToken,
        string pipeName)
    {
        ArgumentNullException.ThrowIfNull(onActivation);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream server = new(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using StreamReader reader = new(
                    server,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 128,
                    leaveOpen: true);
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (TryParsePayload(line, out SettingsActivationRequest? request))
                {
                    onActivation(request!);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // A second host, an endpoint teardown, or a transient pipe
                // failure must not turn this async listener into a synchronous
                // busy loop. Yield before retrying so application startup and
                // shutdown can continue normally.
                try
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal static bool TryParsePayload(
        string? payload,
        out SettingsActivationRequest? request)
    {
        request = null;
        if (payload is not { Length: > 0 and <= 80 })
        {
            return false;
        }

        string[] parts = payload.Split('|', count: 2, StringSplitOptions.TrimEntries);
        if (!Enum.TryParse(parts[0], ignoreCase: true, out SettingsPageKind page) ||
            !Enum.IsDefined(page))
        {
            return false;
        }

        string? token = parts.Length == 2 ? parts[1] : null;
        if (token is not null && !FoxMouse.Platform.Windows.Configuration.SettingsHostReadyChannel.IsValidToken(token))
        {
            return false;
        }

        request = new SettingsActivationRequest(page, token);
        return true;
    }
}
