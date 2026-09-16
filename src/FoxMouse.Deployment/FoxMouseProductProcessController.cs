using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace FoxMouse.Deployment;

public sealed class FoxMouseProductProcessController : IProductProcessController
{
    private readonly Action<string> _restoreCursor;

    public FoxMouseProductProcessController()
        : this(RestoreCursor)
    {
    }

    internal FoxMouseProductProcessController(Action<string> restoreCursor)
    {
        _restoreCursor = restoreCursor ?? throw new ArgumentNullException(nameof(restoreCursor));
    }

    private static readonly IReadOnlyDictionary<string, string> ExpectedRelativePaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FoxMouse"] = "FoxMouse.exe",
            ["FoxMouse.Guard"] = "FoxMouse.Guard.exe",
            ["FoxMouse.Settings"] = Path.Combine("Settings", "FoxMouse.Settings.exe"),
        };

    public bool IsApplicationRunning(string installRoot)
    {
        List<VerifiedProcess> running = EnumerateVerified(installRoot);
        try
        {
            return running.Any(item => item.Role == "FoxMouse");
        }
        finally
        {
            DisposeAll(running);
        }
    }

    public void StopSafely(string installRoot, string recoveryExecutable)
    {
        // Maintenance has a fail-closed cursor postcondition even when the app
        // already crashed and no product process remains to enumerate.
        Exception? maintenanceFailure = null;
        try
        {
            _restoreCursor(recoveryExecutable);
        }
        catch (Exception exception)
        {
            maintenanceFailure = exception;
        }

        // Never remove or replace the running Guard when the initial independent
        // recovery could not establish a visible system cursor.
        if (maintenanceFailure is null)
        {
            try
            {
                StopProcessesAfterInitialRestore(installRoot, recoveryExecutable);
            }
            catch (Exception exception)
            {
                maintenanceFailure = exception;
            }
        }

        try
        {
            _restoreCursor(recoveryExecutable);
        }
        catch (Exception finalRestoreFailure)
        {
            if (maintenanceFailure is not null)
            {
                throw new AggregateException(
                    "FoxMouse maintenance safety and the final cursor recovery both failed.",
                    maintenanceFailure,
                    finalRestoreFailure);
            }

            throw;
        }

        if (maintenanceFailure is not null)
        {
            ExceptionDispatchInfo.Capture(maintenanceFailure).Throw();
        }
    }

    private void StopProcessesAfterInitialRestore(string installRoot, string recoveryExecutable)
    {
        List<VerifiedProcess> running = EnumerateVerified(installRoot);
        try
        {
            if (running.Count == 0)
            {
                return;
            }

            foreach (VerifiedProcess item in Order(running))
            {
                RequestCooperativeExit(item);
            }
        }
        finally
        {
            DisposeAll(running);
        }

        if (WaitForExit(installRoot, TimeSpan.FromSeconds(5)))
        {
            return;
        }

        _restoreCursor(recoveryExecutable);
        List<VerifiedProcess> remaining = EnumerateVerified(installRoot);
        try
        {
            foreach (VerifiedProcess item in Order(remaining))
            {
                if (IsStillVerified(item))
                {
                    item.Process.Kill(entireProcessTree: false);
                }
            }
        }
        finally
        {
            DisposeAll(remaining);
        }

        if (!WaitForExit(installRoot, TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("Verified FoxMouse processes did not exit during maintenance.");
        }

    }

    public void StartApplication(string installRoot)
    {
        string executable = Path.Combine(DeploymentFileSystem.FullPath(installRoot), "FoxMouse.exe");
        using Process process = Process.Start(new ProcessStartInfo(executable, "--background")
        {
            UseShellExecute = true,
            WorkingDirectory = installRoot,
        }) ?? throw new InvalidOperationException("FoxMouse did not start.");
        Thread.Sleep(500);
        if (process.HasExited)
        {
            throw new InvalidOperationException($"FoxMouse exited during startup with code {process.ExitCode}.");
        }
    }

    private static List<VerifiedProcess> EnumerateVerified(string installRoot)
    {
        string root = DeploymentFileSystem.FullPath(installRoot);
        int sessionId = Process.GetCurrentProcess().SessionId;
        List<VerifiedProcess> result = [];
        foreach ((string role, string relativePath) in ExpectedRelativePaths)
        {
            string expectedPath = DeploymentFileSystem.FullPath(Path.Combine(root, relativePath));
            foreach (Process process in Process.GetProcessesByName(role))
            {
                try
                {
                    if (process.HasExited || process.SessionId != sessionId)
                    {
                        process.Dispose();
                        continue;
                    }

                    string? actualPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(actualPath) ||
                        !string.Equals(DeploymentFileSystem.FullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Dispose();
                        continue;
                    }

                    result.Add(new VerifiedProcess(role, expectedPath, sessionId, process));
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    process.Dispose();
                }
            }
        }

        return result;
    }

    private static IEnumerable<VerifiedProcess> Order(IEnumerable<VerifiedProcess> processes) =>
        processes.OrderBy(item => item.Role switch
        {
            "FoxMouse.Settings" => 0,
            "FoxMouse" => 1,
            _ => 2,
        });

    private static void RequestCooperativeExit(VerifiedProcess item)
    {
        if (!IsStillVerified(item))
        {
            return;
        }

        try
        {
            _ = item.Process.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
        }

        if (item.Role != "FoxMouse")
        {
            return;
        }

        try
        {
            foreach (ProcessThread thread in item.Process.Threads)
            {
                using (thread)
                {
                    _ = PostThreadMessage((uint)thread.Id, WmQuit, UIntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static bool IsStillVerified(VerifiedProcess item)
    {
        try
        {
            return !item.Process.HasExited &&
                item.Process.SessionId == item.SessionId &&
                string.Equals(
                    DeploymentFileSystem.FullPath(item.Process.MainModule?.FileName ?? string.Empty),
                    item.ExpectedPath,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ArgumentException)
        {
            return false;
        }
    }

    private static bool WaitForExit(string installRoot, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            List<VerifiedProcess> remaining = EnumerateVerified(installRoot);
            bool empty = remaining.Count == 0;
            DisposeAll(remaining);
            if (empty)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        List<VerifiedProcess> final = EnumerateVerified(installRoot);
        bool exited = final.Count == 0;
        DisposeAll(final);
        return exited;
    }

    private static void RestoreCursor(string recoveryExecutable)
    {
        string executable = DeploymentFileSystem.FullPath(recoveryExecutable);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The independent cursor recovery executable is missing.", executable);
        }

        using Process process = Process.Start(new ProcessStartInfo(executable, "--restore-cursor")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        }) ?? throw new InvalidOperationException("The cursor recovery process did not start.");
        if (!process.WaitForExit(10_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException("Cursor recovery timed out before maintenance.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Cursor recovery failed with exit code {process.ExitCode}.");
        }
    }

    private static void DisposeAll(IEnumerable<VerifiedProcess> processes)
    {
        foreach (VerifiedProcess item in processes)
        {
            item.Process.Dispose();
        }
    }

    private sealed record VerifiedProcess(string Role, string ExpectedPath, int SessionId, Process Process);

    private const uint WmQuit = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
}
