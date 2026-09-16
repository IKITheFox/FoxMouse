using FoxMouse.Settings.Services;

namespace FoxMouse.Settings.Tests;

public sealed class RunningProcessCatalogTests
{
    [Fact]
    public async Task EnumerationReturnsSafeDistinctExecutableNames()
    {
        RunningProcessCatalog catalog = new();

        IReadOnlyList<RunningProcessInfo> processes = await catalog.GetProcessesAsync();

        Assert.NotEmpty(processes);
        Assert.All(processes, process =>
        {
            Assert.EndsWith(".exe", process.ExecutableName, StringComparison.OrdinalIgnoreCase);
            Assert.True(process.InstanceCount > 0);
            Assert.DoesNotContain(Path.DirectorySeparatorChar, process.ExecutableName);
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar, process.ExecutableName);
        });
        Assert.Equal(
            processes.Count,
            processes.Select(process => process.ExecutableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }
}
