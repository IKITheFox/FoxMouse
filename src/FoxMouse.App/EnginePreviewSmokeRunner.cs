using FoxMouse.Core;
using FoxMouse.Platform.Windows.Diagnostics;
using FoxMouse.Platform.Windows.Cursor;

namespace FoxMouse.App;

internal static class EnginePreviewSmokeRunner
{
    private const int WatchdogExitCode = 39;

    internal static int Run(bool verifyStationaryRecovery = false)
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"FoxMouse-Engine-Smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        using ApplicationContext context = new();
        using System.Windows.Forms.Timer startupTimer = new() { Interval = 1 };
        using System.Windows.Forms.Timer observationTimer = new() { Interval = 10 };
        using System.Threading.Timer watchdog = new(
            static _ => Environment.Exit(WatchdogExitCode),
            null,
            TimeSpan.FromSeconds(15),
            Timeout.InfiniteTimeSpan);

        RollingFileLogger logger = new(Path.Combine(temporaryRoot, "Logs"));
        FoxMouseSettings settings = FoxMouseSettings.Default with
        {
            Enabled = true,
            Mode = CursorEffectMode.HighFidelity,
            DisableWhileDragging = false,
            PauseInFullscreen = false,
            ExcludedProcesses = [],
        };
        FoxMouseEngine engine = new(settings, logger);
        long deadline = 0;
        int exitCode = 38;
        bool finishing = false;
        bool replacementObserved = false;
        long handoffObservedAt = 0;
        System.Drawing.Point initialPosition = System.Windows.Forms.Cursor.Position;
        using CursorTracker tracker = new();

        async Task FinishAsync(int requestedExitCode)
        {
            if (finishing)
            {
                return;
            }

            finishing = true;
            startupTimer.Stop();
            observationTimer.Stop();
            int finalExitCode = requestedExitCode;
            try
            {
                await engine.DisposeAsync();
            }
            catch (Exception)
            {
                finalExitCode = 37;
            }

            try
            {
                await logger.DisposeAsync();
            }
            catch (Exception)
            {
                if (finalExitCode == 0)
                {
                    finalExitCode = 36;
                }
            }

            exitCode = finalExitCode;
            context.ExitThread();
        }

        startupTimer.Tick += async (_, _) =>
        {
            startupTimer.Stop();
            try
            {
                await engine.InitializeAsync();
                if (!engine.IsGuardReady)
                {
                    await FinishAsync(31);
                    return;
                }

                initialPosition = System.Windows.Forms.Cursor.Position;
                engine.Preview();
                deadline = Environment.TickCount64 + (verifyStationaryRecovery ? 8_000 : 3_000);
                observationTimer.Start();
            }
            catch (Exception)
            {
                await FinishAsync(32);
            }
        };

        observationTimer.Tick += async (_, _) =>
        {
            if (verifyStationaryRecovery && System.Windows.Forms.Cursor.Position != initialPosition)
            {
                logger.Warning("stationary-smoke-invalid", $"Pointer position changed from {initialPosition} to {System.Windows.Forms.Cursor.Position}; this run cannot prove stationary recovery.");
                await FinishAsync(34);
                return;
            }
            if (engine.IsHighFidelityReplacementActive)
            {
                replacementObserved = true;
                if (!verifyStationaryRecovery) await FinishAsync(0);
            }
            else if (verifyStationaryRecovery && replacementObserved && engine.IsNativeCursorHandoffComplete)
            {
                if (handoffObservedAt == 0) handoffObservedAt = Environment.TickCount64;
                if (Environment.TickCount64 - handoffObservedAt >= 350)
                {
                    bool visible = tracker.TryObserve(out CursorObservation observation) && observation.IsVisible;
                    logger.Information("stationary-smoke-result", $"visible={visible}; position={initialPosition}; handoffComplete=true; replacementObserved=true");
                    await FinishAsync(visible ? 0 : 35);
                }
            }
            if (!finishing && Environment.TickCount64 >= deadline)
            {
                await FinishAsync(33);
            }
        };

        try
        {
            startupTimer.Start();
            Application.Run(context);
            watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return exitCode;
        }
        finally
        {
            watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (!finishing)
            {
                try
                {
                    engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                }

                try
                {
                    logger.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                }
            }

            if (!verifyStationaryRecovery) DeleteTemporaryRoot(temporaryRoot);
        }
    }

    private static void DeleteTemporaryRoot(string temporaryRoot)
    {
        string resolved = Path.GetFullPath(temporaryRoot);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(resolved))
        {
            return;
        }

        try
        {
            Directory.Delete(resolved, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
