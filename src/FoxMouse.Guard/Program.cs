using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using FoxMouse.Platform.Windows.Visibility;

namespace FoxMouse.Guard;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--restore-cursor", StringComparer.OrdinalIgnoreCase))
        {
            using MagnificationCursorController recovery = new();
            bool restored = recovery.TrySetVisible(true);
            if (restored)
            {
                _ = new SystemCursorRefresher().TryRefreshAtCurrentPosition();
            }

            return restored ? 0 : 1;
        }

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            using FakeSystemCursorController fake = new();
            return fake.TryInitialize() && fake.TrySetVisible(false) && fake.TrySetVisible(true) ? 0 : 1;
        }

        string? pipeName = ReadArgument(args, "--pipe");
        string? ownerText = ReadArgument(args, "--owner-pid");
        bool fakeBackend = args.Contains("--fake", StringComparer.OrdinalIgnoreCase);
        bool exitAfterHideBeforeReply = fakeBackend &&
            args.Contains("--test-exit-after-hide-before-reply", StringComparer.OrdinalIgnoreCase);
        bool delayHideThenExitBeforeReply = fakeBackend &&
            args.Contains("--test-delay-hide-then-exit-before-reply", StringComparer.OrdinalIgnoreCase);
        bool delayHideReply = fakeBackend &&
            args.Contains("--test-delay-hide-reply", StringComparer.OrdinalIgnoreCase);
        bool delayShowReply = fakeBackend &&
            args.Contains("--test-delay-show-reply", StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pipeName) ||
            !int.TryParse(ownerText, NumberStyles.None, CultureInfo.InvariantCulture, out int ownerPid) ||
            ownerPid <= 0)
        {
            return 2;
        }

        return await RunAsync(
            pipeName,
            ownerPid,
            fakeBackend,
            exitAfterHideBeforeReply,
            delayHideThenExitBeforeReply,
            delayHideReply,
            delayShowReply).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(
        string pipeName,
        int ownerPid,
        bool fakeBackend,
        bool exitAfterHideBeforeReply,
        bool delayHideThenExitBeforeReply,
        bool delayHideReply,
        bool delayShowReply)
    {
        using Process? owner = TryGetOwner(ownerPid);
        if (owner is null)
        {
            return 3;
        }

        using ISystemCursorController controller = fakeBackend
            ? new FakeSystemCursorController()
            : new MagnificationCursorController();
        bool initialized = controller.TryInitialize();

        using NamedPipeClientStream pipe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(5));
        try
        {
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 4;
        }
        catch (IOException)
        {
            return 4;
        }

        using StreamReader reader = new(pipe, leaveOpen: true);
        using GuardStreamWriter writer = new(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(GuardProtocol.SerializeResponse(
            new GuardResponse("ready", 0, initialized, initialized ? null : "Magnification API unavailable")))
            .ConfigureAwait(false);
        if (!initialized)
        {
            return 5;
        }

        SystemCursorLeaseState cursorState = new(controller);
        using CancellationTokenSource watchdogStop = new();
        Task watchdog = RunLeaseWatchdogAsync(cursorState, watchdogStop.Token);
        Task<string?> pendingRead = reader.ReadLineAsync();
        Task ownerExit = owner.WaitForExitAsync();
        bool ownerExitObserved = false;
        try
        {
            while (true)
            {
                Task completed = await Task.WhenAny(pendingRead, ownerExit).ConfigureAwait(false);
                if (completed == ownerExit)
                {
                    ownerExitObserved = true;
                    break;
                }

                string? line = await pendingRead.ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                GuardRequest? request;
                try
                {
                    request = GuardProtocol.ParseRequest(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    await ReplyAsync(writer, new GuardResponse("invalid", 0, false, "Invalid JSON")).ConfigureAwait(false);
                    pendingRead = reader.ReadLineAsync();
                    continue;
                }

                if (request is null || request.ProtocolVersion != GuardProtocol.Version)
                {
                    await ReplyAsync(writer, new GuardResponse("invalid", 0, false, "Protocol mismatch")).ConfigureAwait(false);
                    pendingRead = reader.ReadLineAsync();
                    continue;
                }

                bool shouldExit = await HandleRequestAsync(
                    cursorState,
                    writer,
                    request,
                    exitAfterHideBeforeReply,
                    delayHideThenExitBeforeReply,
                    delayHideReply,
                    delayShowReply).ConfigureAwait(false);
                if (shouldExit)
                {
                    break;
                }

                pendingRead = reader.ReadLineAsync();
            }
        }
        catch (IOException)
        {
            return 6;
        }
        catch (OperationCanceledException)
        {
            return 6;
        }
        catch (ObjectDisposedException)
        {
            return 6;
        }
        catch (InvalidOperationException)
        {
            return 6;
        }
        finally
        {
            watchdogStop.Cancel();
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the IPC loop finishes.
            }

            // Recovery-only mode: after this process has hidden the cursor it
            // must not voluntarily exit until a show operation succeeds.
            while (cursorState.IsHidden)
            {
                if (cursorState.TryShow())
                {
                    break;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            // WaitForExit completes only after Windows has torn down the
            // owner's overlay window. Refreshing here cannot target that
            // overlay and repairs a stationary cursor after crash recovery.
            if (ownerExitObserved && !fakeBackend)
            {
                _ = new SystemCursorRefresher().TryRefreshAtCurrentPosition();
            }
        }

        return 0;
    }

    private static async Task<bool> HandleRequestAsync(
        SystemCursorLeaseState cursorState,
        StreamWriter writer,
        GuardRequest request,
        bool exitAfterHideBeforeReply,
        bool delayHideThenExitBeforeReply,
        bool delayHideReply,
        bool delayShowReply)
    {
        switch (request.Operation)
        {
            case "hide":
            {
                if (delayHideThenExitBeforeReply)
                {
                    // The client timeout fires first. This intentionally
                    // recreates the dangerous case where a timed-out request
                    // can still hide the cursor after a local recovery attempt.
                    await Task.Delay(1_200).ConfigureAwait(false);
                }

                bool success = cursorState.TryHide(request.Generation, request.LeaseMilliseconds);
                if (success && (exitAfterHideBeforeReply || delayHideThenExitBeforeReply))
                {
                    // Fault injection is accepted only with the fake backend.
                    // It validates the client's Unknown -> idempotent Show
                    // recovery path without ever hiding the real cursor.
                    // Environment.Exit bypasses finally without invoking
                    // Windows Error Reporting, keeping this fake-only fault
                    // deterministic while still simulating an abrupt loss.
                    Environment.Exit(17);
                }

                if (success && delayHideReply)
                {
                    // The cursor is already hidden. Let the client poison and
                    // close IPC, then prove this Guard's finally path restores
                    // before the process exits.
                    await Task.Delay(1_200).ConfigureAwait(false);
                }

                await ReplyAsync(writer, new GuardResponse("hide", request.Generation, success)).ConfigureAwait(false);
                return false;
            }

            case "renew":
            {
                bool success = cursorState.TryRenew(request.Generation, request.LeaseMilliseconds);
                await ReplyAsync(writer, new GuardResponse("renew", request.Generation, success)).ConfigureAwait(false);
                return false;
            }

            case "show":
            {
                bool success = cursorState.TryShow();
                if (success && delayShowReply)
                {
                    // Creates a deterministic in-flight Show acknowledgement
                    // for the client's cross-generation serialization test.
                    await Task.Delay(250).ConfigureAwait(false);
                }

                await ReplyAsync(writer, new GuardResponse("show", request.Generation, success)).ConfigureAwait(false);
                return false;
            }

            case "exit":
            {
                bool success = cursorState.TryShow();
                await ReplyAsync(writer, new GuardResponse("exit", request.Generation, success)).ConfigureAwait(false);
                return success;
            }

            default:
                await ReplyAsync(writer, new GuardResponse(request.Operation, request.Generation, false, "Unknown operation"))
                    .ConfigureAwait(false);
                return false;
        }
    }

    private static async Task RunLeaseWatchdogAsync(SystemCursorLeaseState cursorState, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cursorState.RestoreIfLeaseExpired(Stopwatch.GetTimestamp());
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplyAsync(StreamWriter writer, GuardResponse response)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(100));
        await writer.WriteLineAsync(
            GuardProtocol.SerializeResponse(response).AsMemory(),
            timeout.Token).ConfigureAwait(false);
    }

    private static Process? TryGetOwner(int ownerPid)
    {
        try
        {
            Process owner = Process.GetProcessById(ownerPid);
            using Process current = Process.GetCurrentProcess();
            if (owner.HasExited || owner.SessionId != current.SessionId)
            {
                owner.Dispose();
                return null;
            }

            return owner;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed class GuardStreamWriter(Stream stream) : StreamWriter(stream, leaveOpen: true)
    {
        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            catch (IOException)
            {
                // The client intentionally closes a poisoned pipe. Cursor
                // recovery has already run in RunAsync's finally block.
            }
        }
    }
}
