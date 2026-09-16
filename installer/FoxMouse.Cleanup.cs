using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

internal static class Program
{
    private const string TemporaryHostPrefix = "FoxMouse-Uninstall-Host-";
    private const string WorkerPrefix = "FoxMouse-CleanupWorker-";

    private static int Main(string[] args)
    {
        string statusFile = null;
        try
        {
            Dictionary<string, string> options = Parse(args);
            statusFile = GetOptional(options, "--status-file");
            string targetFile = Full(GetRequired(options, "--target-file"));
            string companionFile = Full(GetRequired(options, "--companion-file"));
            string targetDirectory = Full(GetRequired(options, "--target-directory"));
            string readyEvent = GetRequired(options, "--ready-event");
            int targetProcessId = Int32.Parse(GetRequired(options, "--after-pid"), System.Globalization.CultureInfo.InvariantCulture);
            string testRoot = GetOptional(options, "--test-root");
            string testRegistryRoot = GetOptional(options, "--test-registry-root");

            string settingsRoot;
            ValidateWorkerLocation();
            ValidateTarget(targetFile, companionFile, targetDirectory, testRoot, testRegistryRoot, out settingsRoot);
            if (!String.IsNullOrWhiteSpace(statusFile))
            {
                statusFile = Full(statusFile);
                ValidateStatusFile(statusFile, settingsRoot);
            }

            using (Process target = Process.GetProcessById(targetProcessId))
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    if (target.SessionId != current.SessionId ||
                        !String.Equals(Full(target.MainModule.FileName), targetFile, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Cleanup PID does not identify the verified FoxMouse maintenance host.");
                    }
                }

                using (EventWaitHandle ready = EventWaitHandle.OpenExisting(readyEvent))
                {
                    ready.Set();
                }

                if (!target.WaitForExit(60000))
                {
                    throw new TimeoutException("The FoxMouse maintenance host did not exit before cleanup.");
                }
            }

            Exception lastFailure = null;
            Stopwatch retry = Stopwatch.StartNew();
            do
            {
                try
                {
                    DeleteFileIfPresent(targetFile);
                    DeleteFileIfPresent(companionFile);
                    if (Directory.Exists(targetDirectory))
                    {
                        Directory.Delete(targetDirectory, false);
                    }

                    lastFailure = null;
                    break;
                }
                catch (Exception exception)
                {
                    if (!(exception is IOException) && !(exception is UnauthorizedAccessException))
                    {
                        throw;
                    }

                    lastFailure = exception;
                    Thread.Sleep(100);
                }
            }
            while (retry.Elapsed < TimeSpan.FromSeconds(15));

            if (lastFailure != null || File.Exists(targetFile) || File.Exists(companionFile) || Directory.Exists(targetDirectory))
            {
                throw new IOException("FoxMouse maintenance host cleanup did not complete.", lastFailure);
            }

            if (!String.IsNullOrWhiteSpace(statusFile))
            {
                WriteStatus(statusFile, true, null);
            }

            return 0;
        }
        catch (Exception exception)
        {
            if (!String.IsNullOrWhiteSpace(statusFile))
            {
                try { WriteStatus(Full(statusFile), false, exception.Message); } catch { }
            }

            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Cleanup worker arguments must be name/value pairs.");
            }

            if (result.ContainsKey(args[index]))
            {
                throw new ArgumentException("Duplicate cleanup worker argument: " + args[index]);
            }

            result.Add(args[index], args[index + 1]);
        }

        return result;
    }

    private static string GetRequired(IDictionary<string, string> options, string name)
    {
        string value;
        if (!options.TryGetValue(name, out value) || String.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Missing cleanup worker argument: " + name);
        }

        return value;
    }

    private static string GetOptional(IDictionary<string, string> options, string name)
    {
        string value;
        return options.TryGetValue(name, out value) ? value : null;
    }

    private static void ValidateWorkerLocation()
    {
        string executable = Full(Process.GetCurrentProcess().MainModule.FileName);
        string directory = Full(Path.GetDirectoryName(executable));
        string temporaryRoot = Full(Path.GetTempPath());
        if (!String.Equals(Path.GetDirectoryName(directory), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !HasGuidSuffix(Path.GetFileName(directory), WorkerPrefix) ||
            !String.Equals(Path.GetFileName(executable), "FoxMouse.Cleanup.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cleanup worker is outside its constrained temporary directory.");
        }
    }

    private static void ValidateTarget(
        string targetFile,
        string companionFile,
        string targetDirectory,
        string testRoot,
        string testRegistryRoot,
        out string settingsRoot)
    {
        string directory = Full(targetDirectory);
        if (!String.Equals(targetFile, Full(Path.Combine(directory, "FoxMouse.Uninstall.exe")), StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(companionFile, Full(Path.Combine(directory, "FoxMouse.Cleanup.exe")), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cleanup target filenames are invalid.");
        }

        string temporaryRoot = Full(Path.GetTempPath());
        bool temporaryHost = String.Equals(Path.GetDirectoryName(directory), temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            HasGuidSuffix(Path.GetFileName(directory), TemporaryHostPrefix);

        if (!String.IsNullOrWhiteSpace(testRoot) || !String.IsNullOrWhiteSpace(testRegistryRoot))
        {
            if (!String.Equals(Environment.GetEnvironmentVariable("FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS"), "1", StringComparison.Ordinal) ||
                String.IsNullOrWhiteSpace(testRoot) || String.IsNullOrWhiteSpace(testRegistryRoot))
            {
                throw new InvalidOperationException("Cleanup test paths require explicit opt-in.");
            }

            string isolatedRoot = Full(testRoot);
            if (!String.Equals(Path.GetDirectoryName(isolatedRoot), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !HasGuidSuffix(Path.GetFileName(isolatedRoot), "FoxMouse-InstallerLifecycle-") ||
                !testRegistryRoot.StartsWith("Software\\FoxMouse\\Tests\\InstallerLifecycle-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cleanup test root is outside the isolated namespace.");
            }

            settingsRoot = Full(Path.Combine(isolatedRoot, "LocalAppData", "FoxMouse"));
        }
        else
        {
            settingsRoot = Full(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FoxMouse"));
        }

        string maintenanceRoot = Full(Path.Combine(settingsRoot, "Maintenance"));
        if (!temporaryHost && !String.Equals(directory, maintenanceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cleanup target directory is outside approved FoxMouse roots.");
        }
    }

    private static void ValidateStatusFile(string statusFile, string settingsRoot)
    {
        string file = Full(statusFile);
        string temporaryRoot = Full(Path.GetTempPath());
        string fileName = Path.GetFileName(file);
        bool settingsResult = String.Equals(Path.GetDirectoryName(file), Full(settingsRoot), StringComparison.OrdinalIgnoreCase) &&
            HasGuidFileName(fileName, "cleanup-result-", ".json");
        bool temporaryResult = String.Equals(Path.GetDirectoryName(file), temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            HasGuidFileName(fileName, "FoxMouse-CleanupResult-", ".json");
        if (!settingsResult && !temporaryResult)
        {
            throw new InvalidOperationException("Cleanup status path is outside approved FoxMouse roots.");
        }
    }

    private static bool HasGuidFileName(string fileName, string prefix, string suffix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string value = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return value.Length == 32 && value.All(Uri.IsHexDigit);
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static bool IsInside(string path, string root)
    {
        return Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasGuidSuffix(string leaf, string prefix)
    {
        if (!leaf.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string suffix = leaf.Substring(prefix.Length);
        return suffix.Length == 32 && suffix.All(Uri.IsHexDigit);
    }

    private static string Full(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void WriteStatus(string path, bool success, string error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string candidate = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string workerRoot = Full(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName));
        string json = "{\"schema\":\"foxmouse.cleanup-result/1\",\"success\":" +
            (success ? "true" : "false") + ",\"error\":" + JsonString(error) +
            ",\"workerProcessId\":" + Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"workerRoot\":" + JsonString(workerRoot) +
            ",\"completedUtc\":" + JsonString(DateTimeOffset.UtcNow.ToString("O")) + "}";
        try
        {
            using (FileStream stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            Exception lastFailure = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(candidate, path, null, true);
                    }
                    else
                    {
                        File.Move(candidate, path);
                    }

                    lastFailure = null;
                    break;
                }
                catch (IOException exception)
                {
                    lastFailure = exception;
                    Thread.Sleep(10);
                }
                catch (UnauthorizedAccessException exception)
                {
                    lastFailure = exception;
                    Thread.Sleep(10);
                }
            }
            while (DateTime.UtcNow < deadline);

            if (lastFailure != null)
            {
                throw new IOException("Could not atomically publish cleanup status.", lastFailure);
            }
        }
        finally
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private static string JsonString(string value)
    {
        if (value == null) return "null";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }
}
