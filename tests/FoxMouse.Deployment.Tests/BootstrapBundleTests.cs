using System.IO.Compression;
using FoxMouse.Bootstrap;

namespace FoxMouse.Deployment.Tests;

public sealed class BootstrapBundleTests
{
    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("/outside.exe")]
    [InlineData("C:/outside.exe")]
    [InlineData("safe.exe:stream")]
    [InlineData("sub/../outside.exe")]
    [InlineData("sub\\outside.exe")]
    public void UnsafeEntriesCannotEscapeStaging(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "FoxMouse.Online." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using MemoryStream source = Bundle(name);
            Assert.Throws<InvalidDataException>(() => BootstrapBundle.Extract(source, root));
        }
        finally { BootstrapBundle.DeleteOwnedRoot(root); }
    }

    [Fact]
    public void CompleteBundleExtractsAndOwnedCleanupIsScoped()
    {
        string root = Path.Combine(Path.GetTempPath(), "FoxMouse.Online." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using MemoryStream source = Bundle("sub/content.txt");
            BootstrapBundle.Extract(source, root);
            Assert.Equal("test", File.ReadAllText(Path.Combine(root, "sub", "content.txt")));
            Assert.Throws<InvalidDataException>(() => BootstrapBundle.DeleteOwnedRoot(Path.GetTempPath()));
        }
        finally { BootstrapBundle.DeleteOwnedRoot(root); }
        Assert.False(Directory.Exists(root));
    }

    private static MemoryStream Bundle(string name)
    {
        MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        using (StreamWriter writer = new(zip.CreateEntry(name).Open())) writer.Write("test");
        stream.Position = 0;
        return stream;
    }
}
