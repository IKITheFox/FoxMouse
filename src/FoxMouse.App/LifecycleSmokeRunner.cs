namespace FoxMouse.App;

internal static class LifecycleSmokeRunner
{
    private const int WatchdogExitCode = 24;

    internal static int Run()
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"FoxMouse-Lifecycle-Smoke-{Guid.NewGuid():N}");
        FoxMouseApplicationContext? context = null;
        using System.Threading.Timer watchdog = new(
            static _ => Environment.Exit(WatchdogExitCode),
            null,
            TimeSpan.FromSeconds(12),
            Timeout.InfiniteTimeSpan);

        try
        {
            // This factory neither reads the user's SettingsStore nor touches
            // Run registration. Enabled=false guarantees physical input cannot
            // enter the native-cursor hide protocol during this smoke test.
            context = FoxMouseApplicationContext.CreateForLifecycleSmoke(temporaryRoot);
            Application.Run(context);
            watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            if (!context.LifecycleSmokeInitializationSucceeded)
            {
                return 21;
            }

            if (!context.LifecycleSmokeGuardReady)
            {
                return 22;
            }

            if (!context.ShutdownCompleted)
            {
                return 23;
            }

            return context.LifecycleSmokeDialogClosed ? 0 : 26;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return exception.HResult == 0 ? 25 : Math.Abs(exception.HResult % 100) + 25;
        }
        finally
        {
            watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            context?.Dispose();
            DeleteTemporaryRoot(temporaryRoot);
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
