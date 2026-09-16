using FoxMouse.Core;
using FoxMouse.Platform.Windows.Configuration;
using FoxMouse.Platform.Windows.Cursor;
using FoxMouse.Platform.Windows.Input;
using FoxMouse.Platform.Windows.Rendering;
using FoxMouse.Platform.Windows.Visibility;

namespace FoxMouse.App;

internal static class SmokeTestRunner
{
    internal static int Run(bool allowRealCursorHide)
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"FoxMouse-Smoke-{Guid.NewGuid():N}");
        try
        {
            SettingsStore settingsStore = new(temporaryRoot);
            FoxMouseSettings expected = FoxMouseSettings.Default with
            {
                Sensitivity = 0.63,
                ExcludedProcesses = ["smoke-test.exe"],
            };
            settingsStore.SaveAsync(expected).GetAwaiter().GetResult();
            FoxMouseSettings actual = settingsStore.LoadAsync().GetAwaiter().GetResult();
            if (actual.Sensitivity != expected.Sensitivity || actual.ExcludedProcesses.Length != 1)
            {
                return 11;
            }

            using RawInputSink input = new();
            using CursorTracker tracker = new();
            if (!tracker.TryObserve(out CursorObservation cursor) || !cursor.IsVisible)
            {
                return 12;
            }

            using LayeredCursorOverlay overlay = new();
            overlay.ShowLocator(cursor.Position, 0.75);
            Application.DoEvents();
            Thread.Sleep(25);
            overlay.HideOverlay();

            // Exercise the same multimedia timer lease used by the live
            // render loop so release smoke tests catch native entry-point
            // binding regressions before installation.
            using (HighResolutionTimerLease timerResolution = new())
            {
            }

            using (FakeSystemCursorController fake = new())
            {
                if (!fake.TrySetVisible(false) || !fake.TrySetVisible(true))
                {
                    return 13;
                }
            }

            if (allowRealCursorHide)
            {
                if (cursor.Image is not { SupportsReplacement: true })
                {
                    return 14;
                }

                string? guardPath = FoxMouseEngine.FindGuardExecutable();
                if (guardPath is null)
                {
                    return 15;
                }

                GuardClient guard = new();
                try
                {
                    if (!guard.StartAsync(guardPath).GetAwaiter().GetResult())
                    {
                        return 16;
                    }

                    overlay.ShowCursor(cursor, 1.0);
                    Application.DoEvents();
                    if (!guard.HideAsync(
                            generation: 1,
                            leaseMilliseconds: GuardProtocol.DefaultLeaseMilliseconds)
                        .GetAwaiter()
                        .GetResult())
                    {
                        return 17;
                    }

                    Thread.Sleep(100);
                }
                finally
                {
                    try
                    {
                        _ = guard.ShowAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception)
                    {
                        // GuardClient and its independent Guard retain cursor
                        // recovery responsibility even if this acknowledgement
                        // path fails.
                    }
                    finally
                    {
                        try
                        {
                            guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                        finally
                        {
                            overlay.HideOverlay();
                        }
                    }
                }
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return exception.HResult == 0 ? 20 : Math.Abs(exception.HResult % 100) + 20;
        }
        finally
        {
            string resolved = Path.GetFullPath(temporaryRoot);
            string temp = Path.GetFullPath(Path.GetTempPath());
            if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
