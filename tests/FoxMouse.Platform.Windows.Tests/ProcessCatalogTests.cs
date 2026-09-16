using System.ComponentModel;
using System.Diagnostics;
using FoxMouse.Platform.Windows.Processes;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class ProcessCatalogTests
{
    [Theory]
    [InlineData("foxmouse", "foxmouse.exe")]
    [InlineData(" FoxMouse.EXE ", "FoxMouse.EXE")]
    [InlineData("\"C:\\Program Files\\Fox Mouse\\FoxMouse.exe\"", "FoxMouse.exe")]
    public void NormalizeExecutableNameReturnsExeBasename(string input, string expected)
    {
        Assert.Equal(expected, ProcessCatalog.NormalizeExecutableName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plugin.dll")]
    [InlineData("C:\\Tools\\")]
    [InlineData(".")]
    public void NormalizeExecutableNameRejectsInvalidValues(string? input)
    {
        Assert.Null(ProcessCatalog.NormalizeExecutableName(input));
    }

    [Fact]
    public void MergeExecutableNamesPreservesOrderAndDeduplicatesCanonicalIdentity()
    {
        string[] merged = ProcessCatalog.MergeExecutableNames(
            ["alpha", "C:\\Apps\\Beta.exe", "ALPHA.EXE"],
            ["beta.EXE", "gamma.exe", "notes.txt", "Gamma.EXE"]);

        Assert.Equal(["alpha.exe", "Beta.exe", "gamma.exe"], merged);
    }

    [Theory]
    [InlineData("FoxMouse.exe")]
    [InlineData("FOXMOUSE.GUARD.EXE")]
    [InlineData("foxmouse.settings.exe")]
    public void ProductExecutablesAreNotValidPickerCandidates(string executableName)
    {
        Assert.True(ProcessCatalog.IsFoxMouseExecutable(executableName));
        Assert.Empty(ProcessCatalog.CollapseByExecutable(
        [
            Entry(1, executableName, "FoxMouse", "FoxMouse", visible: true),
        ]));
    }

    [Fact]
    public void FilterMatchesFriendlyNameExecutableAndWindowTitleAcrossTerms()
    {
        ProcessCatalogEntry[] entries =
        [
            Entry(1, "writer.exe", "Paper Writer", "Quarterly plan", visible: true),
            Entry(2, "render-agent.exe", "Render Worker", "", visible: false),
            Entry(3, "browser.exe", "Web Browser", "FoxMouse documentation", visible: true),
        ];

        Assert.Equal(
            ["writer.exe"],
            ProcessCatalog.Filter(entries, "paper plan", includeBackgroundProcesses: false)
                .Select(static entry => entry.ExecutableName));
        Assert.Equal(
            ["browser.exe"],
            ProcessCatalog.Filter(entries, "foxmouse", includeBackgroundProcesses: false)
                .Select(static entry => entry.ExecutableName));
        Assert.Empty(ProcessCatalog.Filter(entries, "render-agent", includeBackgroundProcesses: false));
        Assert.Equal(
            ["render-agent.exe"],
            ProcessCatalog.Filter(entries, "RENDER-AGENT", includeBackgroundProcesses: true)
                .Select(static entry => entry.ExecutableName));
    }

    [Fact]
    public void FilterPrioritizesVisibleApplicationsThenUsesStableNames()
    {
        ProcessCatalogEntry[] entries =
        [
            Entry(1, "zeta.exe", "Zeta", "", visible: false),
            Entry(2, "charlie.exe", "Charlie", "", visible: true),
            Entry(3, "alpha.exe", "Alpha", "", visible: true),
        ];

        Assert.Equal(
            ["alpha.exe", "charlie.exe", "zeta.exe"],
            ProcessCatalog.Filter(entries, null, includeBackgroundProcesses: true)
                .Select(static entry => entry.ExecutableName));
    }

    [Fact]
    public void CollapseByExecutableIsCaseInsensitiveAndKeepsVisibleRichMetadata()
    {
        ProcessCatalogEntry[] entries =
        [
            Entry(10, "sample.exe", "Sample", "Background task", visible: false),
            new ProcessCatalogEntry(
                11,
                "SAMPLE.EXE",
                "Sample Desktop Application",
                "Open document",
                true,
                "C:\\Apps\\sample.exe"),
            Entry(12, "other.exe", "Other", "", visible: false),
        ];

        IReadOnlyList<ProcessCatalogEntry> collapsed = ProcessCatalog.CollapseByExecutable(entries);

        Assert.Equal(2, collapsed.Count);
        ProcessCatalogEntry sample = Assert.Single(
            collapsed,
            static entry => string.Equals(entry.ExecutableName, "sample.exe", StringComparison.OrdinalIgnoreCase));
        Assert.True(sample.IsVisibleApplication);
        Assert.Equal("Sample Desktop Application", sample.FriendlyName);
        Assert.Contains("Background task", sample.WindowTitle, StringComparison.Ordinal);
        Assert.Contains("Open document", sample.WindowTitle, StringComparison.Ordinal);
        Assert.Equal("C:\\Apps\\sample.exe", sample.ExecutablePath);
    }

    [Fact]
    public async Task EnumerateAsyncHonorsPreCanceledToken()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ProcessCatalog catalog = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.EnumerateAsync(cancellation.Token));
    }

    [Fact]
    public async Task EnumerateAsyncDegradesToEmptyWhenProcessSnapshotFails()
    {
        ProcessCatalog catalog = new(
            static () => throw new Win32Exception(5),
            static () => 1);

        IReadOnlyList<ProcessCatalogEntry> entries = await catalog.EnumerateAsync();

        Assert.Empty(entries);
    }

    [Fact]
    public async Task EnumerateAsyncDisposesEveryProcessObjectFromSnapshot()
    {
        Process supplied = Process.GetCurrentProcess();
        int sessionId = supplied.SessionId;
        ProcessCatalog catalog = new(
            () => [supplied],
            () => sessionId);

        _ = await catalog.EnumerateAsync();

        Exception exception = Assert.ThrowsAny<Exception>(() => _ = supplied.Handle);
        Assert.True(
            exception is InvalidOperationException or ObjectDisposedException,
            $"Unexpected disposed Process exception: {exception.GetType().FullName}");
    }

    [Fact]
    public void InspectExecutableProducesBasenameEntryForBrowseSelection()
    {
        string executablePath = Environment.ProcessPath
                                ?? throw new InvalidOperationException("The test process has no executable path.");
        ProcessCatalog catalog = new();

        ProcessCatalogEntry selected = Assert.IsType<ProcessCatalogEntry>(
            catalog.InspectExecutable(executablePath));

        Assert.Equal(Path.GetFileName(executablePath), selected.ExecutableName);
        Assert.False(string.IsNullOrWhiteSpace(selected.DisplayName));
        Assert.Equal(Path.GetFullPath(executablePath), selected.ExecutablePath);
        Assert.False(selected.IsVisibleApplication);
    }

    [WindowsDesktopFact]
    public async Task EnumerateAsyncReturnsUniqueExeBasenamesForCurrentDesktop()
    {
        ProcessCatalog catalog = new();

        IReadOnlyList<ProcessCatalogEntry> entries = await catalog
            .EnumerateAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(entries, entry =>
        {
            Assert.Equal(entry.ExecutableName, Path.GetFileName(entry.ExecutableName));
            Assert.EndsWith(".exe", entry.ExecutableName, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
        });
        Assert.Equal(
            entries.Count,
            entries.Select(static entry => entry.ExecutableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());

        int firstBackground = entries
            .Select(static (entry, index) => (entry, index))
            .Where(static pair => !pair.entry.IsVisibleApplication)
            .Select(static pair => pair.index)
            .DefaultIfEmpty(entries.Count)
            .First();
        Assert.DoesNotContain(entries.Skip(firstBackground), static entry => entry.IsVisibleApplication);
    }

    private static ProcessCatalogEntry Entry(
        int processId,
        string executableName,
        string friendlyName,
        string windowTitle,
        bool visible) =>
        new(processId, executableName, friendlyName, windowTitle, visible);
}
