using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FoxMouse.Platform.Windows.Processes;

public sealed record ProcessCatalogEntry(
    int ProcessId,
    string ExecutableName,
    string FriendlyName,
    string WindowTitle,
    bool IsVisibleApplication,
    string? ExecutablePath = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName)
        ? Path.GetFileNameWithoutExtension(ExecutableName)
        : FriendlyName;
}

public interface IProcessCatalog
{
    Task<IReadOnlyList<ProcessCatalogEntry>> EnumerateAsync(CancellationToken cancellationToken = default);

    ProcessCatalogEntry? InspectExecutable(string executablePath);
}

/// <summary>
/// Produces a fault-tolerant, per-session catalog for the exclusion picker.
/// Process instances are collapsed by executable basename because that is the
/// persisted exclusion contract.
/// </summary>
public sealed class ProcessCatalog : IProcessCatalog
{
    private readonly Func<Process[]> _processProvider;
    private readonly Func<int> _currentSessionIdProvider;

    public ProcessCatalog()
        : this(Process.GetProcesses, GetCurrentSessionId)
    {
    }

    internal ProcessCatalog(
        Func<Process[]> processProvider,
        Func<int> currentSessionIdProvider)
    {
        _processProvider = processProvider ?? throw new ArgumentNullException(nameof(processProvider));
        _currentSessionIdProvider = currentSessionIdProvider ??
                                    throw new ArgumentNullException(nameof(currentSessionIdProvider));
    }

    public Task<IReadOnlyList<ProcessCatalogEntry>> EnumerateAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => EnumerateCurrentSession(cancellationToken), cancellationToken);

    public ProcessCatalogEntry? InspectExecutable(string executablePath)
    {
        string? executableName = NormalizeExecutableName(executablePath);
        if (executableName is null || IsFoxMouseExecutable(executableName))
        {
            return null;
        }

        string? fullPath = TryGetFullPath(executablePath);
        string friendlyName = TryGetFriendlyName(fullPath) ??
                              Path.GetFileNameWithoutExtension(executableName);
        return new ProcessCatalogEntry(
            0,
            executableName,
            friendlyName,
            string.Empty,
            false,
            fullPath);
    }

    public static IReadOnlyList<ProcessCatalogEntry> Filter(
        IEnumerable<ProcessCatalogEntry> entries,
        string? searchText,
        bool includeBackgroundProcesses)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string[] terms = (searchText ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return entries
            .Where(entry => includeBackgroundProcesses || entry.IsVisibleApplication)
            .Where(entry => terms.Length == 0 || terms.All(term => Matches(entry, term)))
            .OrderByDescending(static entry => entry.IsVisibleApplication)
            .ThenBy(static entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static entry => entry.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string[] MergeExecutableNames(
        IEnumerable<string>? existingNames,
        IEnumerable<string>? addedNames)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        Add(existingNames);
        Add(addedNames);
        return result.ToArray();

        void Add(IEnumerable<string>? names)
        {
            if (names is null)
            {
                return;
            }

            foreach (string name in names)
            {
                string? normalized = NormalizeExecutableName(name);
                if (normalized is not null && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }
        }
    }

    public static string? NormalizeExecutableName(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return null;
        }

        try
        {
            string fileName = Path.GetFileName(pathOrName.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            if (fileName is "." or ".." || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }

            string extension = Path.GetExtension(fileName);
            if (extension.Length == 0)
            {
                fileName += ".exe";
            }
            else if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fileName;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<ProcessCatalogEntry> CollapseByExecutable(
        IEnumerable<ProcessCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<string, ProcessCatalogEntry> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessCatalogEntry candidate in entries)
        {
            string? executableName = NormalizeExecutableName(candidate.ExecutableName);
            if (executableName is null || IsFoxMouseExecutable(executableName))
            {
                continue;
            }

            ProcessCatalogEntry normalized = candidate with { ExecutableName = executableName };
            if (!unique.TryGetValue(executableName, out ProcessCatalogEntry? existing))
            {
                unique.Add(executableName, normalized);
                continue;
            }

            unique[executableName] = MergeEntries(existing, normalized);
        }

        return Filter(unique.Values, searchText: null, includeBackgroundProcesses: true);
    }

    private IReadOnlyList<ProcessCatalogEntry> EnumerateCurrentSession(
        CancellationToken cancellationToken)
    {
        int currentSessionId;
        Process[] processes;
        try
        {
            currentSessionId = _currentSessionIdProvider();
            processes = _processProvider();
        }
        catch (Exception exception) when (IsExpectedProcessException(exception))
        {
            return [];
        }

        List<ProcessCatalogEntry> entries = new(processes.Length);
        try
        {
            foreach (Process process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessCatalogEntry? entry = TryCreateEntry(process, currentSessionId);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }
        finally
        {
            // Process lazily opens native handles. Dispose every object returned
            // by GetProcesses, including objects not visited after cancellation.
            foreach (Process process in processes)
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                    // Catalog refresh must remain best-effort.
                }
            }
        }

        return CollapseByExecutable(entries);
    }

    private static int GetCurrentSessionId()
    {
        using Process current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private static ProcessCatalogEntry? TryCreateEntry(Process process, int currentSessionId)
    {
        int processId;
        try
        {
            if (process.SessionId != currentSessionId)
            {
                return null;
            }

            processId = process.Id;
            if (processId == 0)
            {
                return null;
            }
        }
        catch (Exception exception) when (IsExpectedProcessException(exception))
        {
            return null;
        }

        string? executablePath = TryGetExecutablePath(process);
        string? executableName = NormalizeExecutableName(executablePath) ??
                                 TryGetExecutableName(process);
        if (executableName is null || IsFoxMouseExecutable(executableName))
        {
            return null;
        }

        nint window = TryGetMainWindowHandle(process);
        bool isVisibleApplication = window != nint.Zero && IsWindowVisible(window);
        string windowTitle = TryGetWindowTitle(process);
        string friendlyName = TryGetFriendlyName(executablePath) ??
                              Path.GetFileNameWithoutExtension(executableName);

        return new ProcessCatalogEntry(
            processId,
            executableName,
            friendlyName,
            windowTitle,
            isVisibleApplication,
            executablePath);
    }

    private static ProcessCatalogEntry MergeEntries(
        ProcessCatalogEntry existing,
        ProcessCatalogEntry candidate)
    {
        ProcessCatalogEntry preferred = Score(candidate) > Score(existing) ? candidate : existing;
        string friendlyName = PickRicher(existing.FriendlyName, candidate.FriendlyName);
        string windowTitle = MergeWindowTitles(existing.WindowTitle, candidate.WindowTitle);
        return preferred with
        {
            FriendlyName = friendlyName,
            WindowTitle = windowTitle,
            IsVisibleApplication = existing.IsVisibleApplication || candidate.IsVisibleApplication,
            ExecutablePath = existing.ExecutablePath ?? candidate.ExecutablePath,
        };
    }

    private static int Score(ProcessCatalogEntry entry) =>
        (entry.IsVisibleApplication ? 8 : 0) +
        (!string.IsNullOrWhiteSpace(entry.WindowTitle) ? 4 : 0) +
        (!string.IsNullOrWhiteSpace(entry.FriendlyName) ? 2 : 0) +
        (!string.IsNullOrWhiteSpace(entry.ExecutablePath) ? 1 : 0);

    private static string PickRicher(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return second.Length > first.Length ? second : first;
    }

    private static string MergeWindowTitles(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second) ||
            first.Contains(second, StringComparison.CurrentCultureIgnoreCase))
        {
            return first;
        }

        const int maximumLength = 240;
        string merged = $"{first} · {second}";
        return merged.Length <= maximumLength ? merged : merged[..maximumLength];
    }

    private static bool Matches(ProcessCatalogEntry entry, string term) =>
        entry.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
        entry.ExecutableName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        entry.WindowTitle.Contains(term, StringComparison.CurrentCultureIgnoreCase);

    internal static bool IsFoxMouseExecutable(string executableName) =>
        string.Equals(executableName, "FoxMouse.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(executableName, "FoxMouse.Guard.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(executableName, "FoxMouse.Settings.exe", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetExecutableName(Process process)
    {
        try
        {
            string processName = process.ProcessName;
            return NormalizeExecutableName(processName);
        }
        catch (Exception exception) when (IsExpectedProcessException(exception))
        {
            return null;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (IsExpectedProcessException(exception))
        {
            return null;
        }
    }

    private static nint TryGetMainWindowHandle(Process process)
    {
        try
        {
            return process.MainWindowHandle;
        }
        catch (Exception exception) when (IsExpectedProcessException(exception))
        {
            return nint.Zero;
        }
    }

    private static string TryGetWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle?.Trim() ?? string.Empty;
        }
        catch (Exception exception) when (IsExpectedProcessException(exception))
        {
            return string.Empty;
        }
    }

    private static string? TryGetFriendlyName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(executablePath);
            string? description = version.FileDescription?.Trim();
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or Win32Exception
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? TryGetFullPath(string executablePath)
    {
        try
        {
            return File.Exists(executablePath) ? Path.GetFullPath(executablePath) : null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsExpectedProcessException(Exception exception) => exception is
        ArgumentException or
        InvalidOperationException or
        NotSupportedException or
        Win32Exception or
        UnauthorizedAccessException;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
}
