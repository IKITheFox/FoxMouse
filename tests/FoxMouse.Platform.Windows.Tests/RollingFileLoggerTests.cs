using System.Text;
using FoxMouse.Platform.Windows.Diagnostics;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class RollingFileLoggerTests
{
    [Fact]
    public async Task LogEntryFlattensLineBreaksAndTabsAndTruncatesFields()
    {
        using TemporaryDirectory temporary = new();
        string eventName = $"event\r\n\t{new string('e', 120)}";
        string detail = $"detail\r\n\t{new string('d', 600)}";

        await using (RollingFileLogger logger = new(temporary.Path))
        {
            logger.Warning(eventName, detail);
        }

        string line = Assert.Single(await File.ReadAllLinesAsync(
            System.IO.Path.Combine(temporary.Path, "FoxMouse.log"),
            Encoding.UTF8));
        string[] fields = line.Split('\t');
        Assert.Equal(4, fields.Length);
        Assert.True(DateTimeOffset.TryParse(fields[0], out _));
        Assert.Equal("WARN", fields[1]);
        Assert.Equal(96, fields[2].Length);
        Assert.Equal(512, fields[3].Length);
        Assert.DoesNotContain('\r', fields[2]);
        Assert.DoesNotContain('\n', fields[2]);
        Assert.DoesNotContain('\r', fields[3]);
        Assert.DoesNotContain('\n', fields[3]);
    }

    [Fact]
    public async Task FullLogRotatesAndRetainsOnlyConfiguredBackups()
    {
        using TemporaryDirectory temporary = new();
        string currentPath = System.IO.Path.Combine(temporary.Path, "FoxMouse.log");
        byte[] fullCurrent = Enumerable.Repeat((byte)'x', 64 * 1024).ToArray();
        await File.WriteAllBytesAsync(currentPath, fullCurrent);
        await File.WriteAllTextAsync($"{currentPath}.1", "previous-backup", Encoding.UTF8);
        await File.WriteAllTextAsync($"{currentPath}.2", "expired-backup", Encoding.UTF8);

        await using (RollingFileLogger logger = new(
                         temporary.Path,
                         maximumFileBytes: 1,
                         maximumFiles: 2))
        {
            logger.Information("after-rotation");
        }

        Assert.Equal(fullCurrent, await File.ReadAllBytesAsync($"{currentPath}.1"));
        Assert.Equal("previous-backup", await File.ReadAllTextAsync($"{currentPath}.2", Encoding.UTF8));
        Assert.False(File.Exists($"{currentPath}.3"));
        Assert.Contains("after-rotation", await File.ReadAllTextAsync(currentPath, Encoding.UTF8));
    }

    [Fact]
    public async Task HighVolumeLoggingKeepsFileCountAndSizeBounded()
    {
        using TemporaryDirectory temporary = new();
        const int configuredBackups = 3;
        const long minimumCapacity = 64 * 1024;
        string eventName = new('e', 120);
        string detail = new('d', 600);

        await using (RollingFileLogger logger = new(
                         temporary.Path,
                         maximumFileBytes: 1,
                         maximumFiles: configuredBackups))
        {
            for (int index = 0; index < 800; index++)
            {
                logger.Information(eventName, $"{index:D4}-{detail}");
            }
        }

        FileInfo[] logs = new DirectoryInfo(temporary.Path)
            .GetFiles("FoxMouse.log*")
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(configuredBackups + 1, logs.Length);
        Assert.DoesNotContain(logs, file => file.Name == "FoxMouse.log.4");
        Assert.All(logs, file => Assert.InRange(file.Length, 1, minimumCapacity + 700));
    }

    [Fact]
    public async Task WriterFailureDegradesToNoOpAndConcurrentDisposeIsIdempotent()
    {
        using TemporaryDirectory temporary = new();
        string fileInPlaceOfDirectory = System.IO.Path.Combine(temporary.Path, "not-a-directory");
        await File.WriteAllTextAsync(fileInPlaceOfDirectory, "occupied", Encoding.UTF8);
        RollingFileLogger logger = new(fileInPlaceOfDirectory);
        logger.Information("this-cannot-be-written");

        Task[] disposals = Enumerable.Range(0, 8)
            .Select(_ => logger.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals);

        logger.Warning("writes-after-dispose-are-safe");
        await logger.DisposeAsync();
        Assert.Equal("occupied", await File.ReadAllTextAsync(fileInPlaceOfDirectory, Encoding.UTF8));
    }

    [Fact]
    public async Task ErrorLogDoesNotPersistExceptionMessageOrLocalPath()
    {
        using TemporaryDirectory temporary = new();
        const string sensitivePath = @"C:\Users\Example\Private\document.txt";

        await using (RollingFileLogger logger = new(temporary.Path))
        {
            logger.Error("read-failed", new IOException($"Could not read {sensitivePath}"));
        }

        string text = await File.ReadAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "FoxMouse.log"),
            Encoding.UTF8);
        Assert.Contains("IOException", text, StringComparison.Ordinal);
        Assert.Contains("hresult=0x", text, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitivePath, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Could not read", text, StringComparison.Ordinal);
    }
}
