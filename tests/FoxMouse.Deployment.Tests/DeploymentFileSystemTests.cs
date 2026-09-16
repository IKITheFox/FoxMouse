using System.Diagnostics;
using FoxMouse.Deployment;

namespace FoxMouse.Deployment.Tests;

public sealed class DeploymentFileSystemTests
{
    [Fact]
    public async Task MoveDirectorySurvivesABoundedTransientFileHandle()
    {
        string root = Path.Combine(Path.GetTempPath(), "FoxMouse-MoveRetry-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        string marker = Path.Combine(source, "marker.txt");
        File.WriteAllText(marker, "transient handle");

        FileStream blocker = new(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task release = Task.Run(async () =>
        {
            await Task.Delay(250);
            blocker.Dispose();
        });

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            DeploymentFileSystem.MoveDirectory(source, destination);
            stopwatch.Stop();
            await release;

            Assert.False(Directory.Exists(source));
            Assert.Equal("transient handle", File.ReadAllText(Path.Combine(destination, "marker.txt")));
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        }
        finally
        {
            blocker.Dispose();
            await release;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
