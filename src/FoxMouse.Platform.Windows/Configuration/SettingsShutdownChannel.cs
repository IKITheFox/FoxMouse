using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace FoxMouse.Platform.Windows.Configuration;

/// <summary>Same-user, same-session shutdown coordination scoped to the settings file.</summary>
public static class SettingsShutdownChannel
{
    private static string PipeName(string settingsPath)
    {
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
        return "FoxMouse.Settings.Shutdown.v1." + process.SessionId + "." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(settingsPath).ToUpperInvariant())));
    }

    public static async Task RequestAsync(string settingsPath, CancellationToken cancellationToken = default)
    {
        await using NamedPipeClientStream client = new(".", PipeName(settingsPath), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(750, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return; // No settings host is running.
        }
        await client.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        // A user can take arbitrarily long to decide whether to save. Never
        // terminate the settings process because a dialog deadline expired.
        byte[] reply = new byte[1];
        _ = await client.ReadAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ListenAsync(string settingsPath, Func<Task> closeRequested,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream server = new(PipeName(settingsPath), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                byte[] request = new byte[1];
                using CancellationTokenSource readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                if (await server.ReadAsync(request, readTimeout.Token).ConfigureAwait(false) == 1 && request[0] == 1)
                {
                    await closeRequested().ConfigureAwait(false);
                    await server.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                if (cancellationToken.IsCancellationRequested) return;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
