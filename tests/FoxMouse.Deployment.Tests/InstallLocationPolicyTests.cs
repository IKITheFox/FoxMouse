using System.Text.Json;
using FoxMouse.Deployment;
using Microsoft.Win32;

namespace FoxMouse.Deployment.Tests;

public sealed class InstallLocationPolicyTests : IDisposable
{
    private readonly string _testId = Guid.NewGuid().ToString("N");
    private readonly string _root;
    private readonly string _registryRoot;
    private readonly DeploymentPaths _paths;

    public InstallLocationPolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"FoxMouse-InstallerLifecycle-{_testId}");
        _registryRoot = $@"Software\FoxMouse\Tests\InstallerLifecycle-{_testId}";
        Directory.CreateDirectory(_root);
        _paths = DeploymentPaths.ForIsolatedTestRoot(_root, _registryRoot);
    }

    [Fact]
    public void ParentSelectionAppendsExactlyOneFoxMouseLeaf()
    {
        string parent = Path.Combine(_root, "Applications");

        string result = InstallLocationPolicy.ResolveInstallRoot(parent, _paths);

        Assert.Equal(Path.Combine(parent, "FoxMouse"), result, ignoreCase: true);
    }

    [Fact]
    public void ExistingFoxMouseLeafIsNotDuplicated()
    {
        string selected = Path.Combine(_root, "Applications", "fOxMoUsE");

        string result = InstallLocationPolicy.ResolveInstallRoot(selected, _paths);

        Assert.Equal(Path.GetFullPath(selected), result, ignoreCase: true);
        Assert.False(result.EndsWith(Path.Combine("FoxMouse", "FoxMouse"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RelativeAndUncParentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            InstallLocationPolicy.ResolveInstallRoot(Path.Combine("relative", "apps"), _paths));
        Assert.Throws<ArgumentException>(() =>
            InstallLocationPolicy.ResolveInstallRoot(@"\\server\share", _paths));
    }

    [Fact]
    public void IsolatedSelectionCannotEscapeItsTestRoot()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            InstallLocationPolicy.ResolveInstallRoot(outside, _paths));

        Assert.Contains("escapes", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsAndCacheLocationsCannotOverlapInstallRoot()
    {
        string settingsParent = Path.GetDirectoryName(_paths.SettingsRoot)!;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            InstallLocationPolicy.ResolveInstallRoot(settingsParent, _paths));

        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEmptyForeignTargetIsRejectedWithoutDeletingItsContents()
    {
        string parent = Path.Combine(_root, "ForeignParent");
        string target = Path.Combine(parent, "FoxMouse");
        string sentinel = Path.Combine(target, "do-not-touch.txt");
        Directory.CreateDirectory(target);
        File.WriteAllText(sentinel, "owner-data");

        Assert.Throws<InvalidOperationException>(() =>
            InstallLocationPolicy.ResolveInstallRoot(parent, _paths));

        Assert.Equal("owner-data", File.ReadAllText(sentinel));
    }

    [Fact]
    public void FileBlockingParentPathIsRejectedAsUnwritable()
    {
        string blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "file");
        string selectedParent = Path.Combine(blocker, "Applications");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            InstallLocationPolicy.ResolveInstallRoot(selectedParent, _paths));

        Assert.Contains("file blocks", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedWindowsDirectoryIsRejected()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrWhiteSpace(windows));
        DeploymentPaths production = DeploymentPaths.ForCurrentUser();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            InstallLocationPolicy.ResolveInstallRoot(windows, production));

        Assert.Contains("protected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReparsePointParentIsRejectedWhenSymbolicLinksAreAvailable()
    {
        string actual = Path.Combine(_root, "Actual");
        string link = Path.Combine(_root, "Linked");
        Directory.CreateDirectory(actual);
        try
        {
            Directory.CreateSymbolicLink(link, actual);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            InstallLocationPolicy.ResolveInstallRoot(link, _paths));

        Assert.Contains("Reparse-point", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
        if (Directory.Exists(_root))
        {
            try
            {
                DeploymentFileSystem.EnsureNoReparsePoints(_root);
                Directory.Delete(_root, recursive: true);
            }
            catch (InvalidOperationException)
            {
                string link = Path.Combine(_root, "Linked");
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
