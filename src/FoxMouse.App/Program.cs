namespace FoxMouse.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);
        Application.EnableVisualStyles();
        Application.SetColorMode(SystemColorMode.System);

        if (args.Contains("--stationary-cursor-smoke", StringComparer.OrdinalIgnoreCase))
        {
            if (!args.Contains("--allow-real-cursor-hide", StringComparer.OrdinalIgnoreCase)) return 40;
            using Mutex stationaryMutex = new(initiallyOwned: true, "Local\\FoxMouse.App.v1", out bool ownsStationaryMutex);
            if (!ownsStationaryMutex) return 41;
            try { return EnginePreviewSmokeRunner.Run(verifyStationaryRecovery: true); }
            finally { stationaryMutex.ReleaseMutex(); }
        }

        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            bool allowRealHide = args.Contains("--allow-real-cursor-hide", StringComparer.OrdinalIgnoreCase);
            int smokeResult = SmokeTestRunner.Run(allowRealHide);
            return smokeResult == 0 && allowRealHide
                ? EnginePreviewSmokeRunner.Run()
                : smokeResult;
        }

        if (args.Contains("--lifecycle-smoke", StringComparer.OrdinalIgnoreCase))
        {
            return LifecycleSmokeRunner.Run();
        }

        int trayMenuSmokeIndex = Array.FindIndex(
            args,
            argument => string.Equals(argument, "--tray-menu-smoke", StringComparison.OrdinalIgnoreCase));
        if (trayMenuSmokeIndex >= 0)
        {
            string? outputPath = trayMenuSmokeIndex + 1 < args.Length
                && !args[trayMenuSmokeIndex + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[trayMenuSmokeIndex + 1]
                    : null;
            return TrayMenuVisualSmokeRunner.Run(outputPath);
        }

        int recordIndex = Array.FindIndex(
            args,
            argument => string.Equals(argument, "--record-trace", StringComparison.OrdinalIgnoreCase));
        if (recordIndex >= 0)
        {
            if (recordIndex + 1 >= args.Length)
            {
                return 2;
            }

            return MotionTraceRecorder.Run(args[recordIndex + 1], TimeSpan.FromSeconds(5));
        }

        using Mutex instanceMutex = new(initiallyOwned: true, "Local\\FoxMouse.App.v1", out bool ownsMutex);
        if (!ownsMutex)
        {
            return 0;
        }

        FoxMouseApplicationContext? context = null;
        try
        {
            context = FoxMouseApplicationContext.Create();
            Application.Run(context);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FoxMouse.Core.UiText.Format("AppStartError", exception.Message),
                "FoxMouse",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            context?.Dispose();
            instanceMutex.ReleaseMutex();
        }
    }
}
