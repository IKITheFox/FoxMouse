using System.Diagnostics;
using System.Security.Cryptography;
using FoxMouse.Bootstrap;

namespace FoxMouse.Deployment.Tests;

public sealed class BootstrapExecutionTests
{
    [Theory]
    [InlineData(@"D:\Apps", @"D:\Apps\")]
    [InlineData(@"D:\Apps\FoxMouse", @"D:\Apps\")]
    [InlineData(@"D:\", @"D:\")]
    public void FolderChoiceAppendsProductOnlyOnce(string selected, string expected)
    {
        Assert.Equal(expected, BootstrapWindow.NormalizeInstallParent(selected));
    }

    [Fact]
    public void HandoffPreservesFolderLanguageAndMaintenance()
    {
        string arguments = BootstrapWindow.BuildSetupArguments(@"C:\Temp\package.zip", true, @"D:\My Apps\");
        Assert.Contains("--install-parent \"D:\\My Apps\\.\"", arguments);
        Assert.Contains("--language en-US", arguments);
        Assert.DoesNotContain("--install-parent", BootstrapWindow.BuildSetupArguments("package.zip", false, ""));
    }

    [Fact]
    public void OnlyDependencyWindowIsHiddenAndElevationRemainsAvailable()
    {
        var dependency = BootstrapWindow.CreateStartInfo(@"C:\Temp\runtime.exe", "--quiet", true);
        Assert.True(dependency.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, dependency.WindowStyle);
        Assert.Equal(ProcessWindowStyle.Normal, BootstrapWindow.CreateStartInfo(@"C:\Temp\setup.exe", "", false).WindowStyle);
    }

    [Fact]
    public async Task CancellationNeverExecutesVerifiedFile()
    {
        bool executed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BootstrapDownloader.RunVerifiedAsync(
            "not-opened.exe", "unused", _ => { executed = true; return Task.FromResult(0); }, new CancellationToken(true)));
        Assert.False(executed);
    }

    [Fact]
    public async Task SharingConflictRetriesAreBounded()
    {
        string file = Path.GetTempFileName();
        try
        {
            int attempts = 0;
            string hash = Convert.ToHexString(SHA512.HashData(Array.Empty<byte>()));
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                BootstrapDownloader.RunVerifiedAsync(file, hash, _ =>
                {
                    attempts++;
                    throw new System.ComponentModel.Win32Exception(32);
                }, CancellationToken.None));
            Assert.Equal(3, attempts);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task VerifiedReadLeaseAllowsRealExecutableLaunchAndRejectsWriters()
    {
        string root = Path.Combine(Path.GetTempPath(), "FoxMouse.ExecutionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "where.exe");
        try
        {
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"), file);
            string hash = Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(file)));
            int result = await BootstrapDownloader.RunVerifiedAsync(file, hash, async path =>
            {
                Assert.Throws<IOException>(() =>
                {
                    using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });
                using Process process = Process.Start(new ProcessStartInfo(path, "/?")
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                })!;
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode;
            }, CancellationToken.None);
            Assert.Equal(0, result);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TamperedFileNeverExecutes()
    {
        string file = Path.GetTempFileName();
        try
        {
            bool executed = false;
            await Assert.ThrowsAnyAsync<Exception>(() => BootstrapDownloader.RunVerifiedAsync(file,
                Convert.ToHexString(SHA512.HashData(new byte[] { 1 })), _ =>
                {
                    executed = true;
                    return Task.FromResult(0);
                }, CancellationToken.None));
            Assert.False(executed);
        }
        finally { File.Delete(file); }
    }
}
