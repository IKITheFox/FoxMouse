using FoxMouse.Platform.Windows.Processes;

namespace FoxMouse.Settings.Services;

public sealed record RunningProcessInfo(
    string DisplayName,
    string ExecutableName,
    string? FullPath,
    int InstanceCount,
    string WindowTitle,
    bool IsVisibleApplication);

public interface IRunningProcessCatalog
{
    Task<IReadOnlyList<RunningProcessInfo>> GetProcessesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the shared, fault-tolerant Windows process catalog for the WinUI picker.
/// The shared catalog collapses process instances by executable because exclusions
/// are persisted by executable basename.
/// </summary>
public sealed class RunningProcessCatalog : IRunningProcessCatalog
{
    private readonly IProcessCatalog _catalog;

    public RunningProcessCatalog()
        : this(new ProcessCatalog())
    {
    }

    internal RunningProcessCatalog(IProcessCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<IReadOnlyList<RunningProcessInfo>> GetProcessesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProcessCatalogEntry> entries =
            await _catalog.EnumerateAsync(cancellationToken).ConfigureAwait(false);

        return entries.Select(static entry => new RunningProcessInfo(
                entry.DisplayName,
                entry.ExecutableName,
                entry.ExecutablePath,
                InstanceCount: 1,
                entry.WindowTitle,
                entry.IsVisibleApplication))
            .ToArray();
    }
}
