using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FoxMouse.Platform.Windows.Configuration;

public sealed record SettingsChangeMessage(
    int ProtocolVersion,
    string Command,
    string SettingsPath,
    string Revision,
    DateTimeOffset SentAtUtc)
{
    public const int CurrentProtocolVersion = 1;
    public const string ReloadSettingsCommand = "reload-settings";
    public const string PreviewCommand = "preview";

    public static SettingsChangeMessage CreateReload(string settingsPath) => new(
        CurrentProtocolVersion,
        ReloadSettingsCommand,
        Path.GetFullPath(settingsPath),
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow);

    public static SettingsChangeMessage CreatePreview(string settingsPath) => new(
        CurrentProtocolVersion,
        PreviewCommand,
        Path.GetFullPath(settingsPath),
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow);
}

public static class SettingsChangeChannel
{
    public const string DefaultPipeName = "FoxMouse.SettingsChanged.v1";
    public const int MaximumMessageCharacters = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<bool> NotifyReloadAsync(
        string settingsPath,
        CancellationToken cancellationToken = default,
        string pipeName = DefaultPipeName,
        int connectTimeoutMilliseconds = 350)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        return await NotifyAsync(
            SettingsChangeMessage.CreateReload(settingsPath),
            cancellationToken,
            pipeName,
            connectTimeoutMilliseconds).ConfigureAwait(false);
    }

    public static async Task<bool> NotifyPreviewAsync(
        string settingsPath,
        CancellationToken cancellationToken = default,
        string pipeName = DefaultPipeName,
        int connectTimeoutMilliseconds = 350)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        return await NotifyAsync(
            SettingsChangeMessage.CreatePreview(settingsPath),
            cancellationToken,
            pipeName,
            connectTimeoutMilliseconds).ConfigureAwait(false);
    }

    private static async Task<bool> NotifyAsync(
        SettingsChangeMessage message,
        CancellationToken cancellationToken,
        string pipeName,
        int connectTimeoutMilliseconds)
    {
        ValidatePipeName(pipeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connectTimeoutMilliseconds);

        string json = JsonSerializer.Serialize(message, SerializerOptions);
        if (json.Length > MaximumMessageCharacters)
        {
            throw new InvalidOperationException("The settings notification is unexpectedly large.");
        }

        try
        {
            await using NamedPipeClientStream client = new(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            await using StreamWriter writer = new(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task ListenAsync(
        Func<SettingsChangeMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken,
        string pipeName = DefaultPipeName)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        ValidatePipeName(pipeName);

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
                    bufferSize: 1024,
                    leaveOpen: true);
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                SettingsChangeMessage? message = TryParse(line);
                if (message is not null)
                {
                    await onMessage(message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A settings client can disappear between connect and write.
                // Recreate the server and continue accepting notifications.
            }
            catch (DecoderFallbackException) when (!cancellationToken.IsCancellationRequested)
            {
                // Ignore malformed UTF-8 from a same-user client.
            }
        }
    }

    public static SettingsChangeMessage? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumMessageCharacters)
        {
            return null;
        }

        try
        {
            SettingsChangeMessage? message = JsonSerializer.Deserialize<SettingsChangeMessage>(line, SerializerOptions);
            bool supportedCommand = message?.Command is
                SettingsChangeMessage.ReloadSettingsCommand or SettingsChangeMessage.PreviewCommand;
            return message is { ProtocolVersion: SettingsChangeMessage.CurrentProtocolVersion } &&
                supportedCommand &&
                !string.IsNullOrWhiteSpace(message.SettingsPath) &&
                !string.IsNullOrWhiteSpace(message.Revision)
                ? message
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("The pipe name cannot contain path separators.", nameof(pipeName));
        }
    }
}
