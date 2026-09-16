using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FoxMouse.App.UI;
using FoxMouse.Settings.Branding;

namespace FoxMouse.Settings.Tests;

[Collection(BrandingAssetCollection.Name)]
public sealed class BrandingAssetTests
{
    private static readonly int[] ExpectedIconSizes = [16, 20, 24, 32, 48, 64, 128, 256];

    [Fact]
    public void ThemeMappingSelectsColorDarkAndMonochromeVariants()
    {
        SemanticColors light = SemanticColors.Create(WindowsThemeKind.Light);
        SemanticColors dark = SemanticColors.Create(WindowsThemeKind.Dark);
        SemanticColors highContrastBlack = SemanticColors.Create(WindowsThemeKind.HighContrast) with
        {
            Text = Color.Black,
        };
        SemanticColors highContrastWhite = SemanticColors.Create(WindowsThemeKind.HighContrast) with
        {
            Text = Color.White,
        };

        Assert.Equal(BrandIconVariant.Light, FoxMouseIconFactory.ResolveBrandIconVariant(light));
        Assert.Equal(BrandIconVariant.Dark, FoxMouseIconFactory.ResolveBrandIconVariant(dark));
        Assert.Equal(
            BrandIconVariant.HighContrastBlack,
            FoxMouseIconFactory.ResolveBrandIconVariant(highContrastBlack));
        Assert.Equal(
            BrandIconVariant.HighContrastWhite,
            FoxMouseIconFactory.ResolveBrandIconVariant(highContrastWhite));
    }

    [Fact]
    public void EveryThemeIconAndMarkIsEmbeddedAndDecodable()
    {
        HashSet<string> iconSignatures = [];
        foreach (BrandIconVariant variant in Enum.GetValues<BrandIconVariant>())
        {
            using Icon icon = FoxMouseIconFactory.CreateApplicationIcon(variant);
            using Bitmap bitmap = icon.ToBitmap();
            Assert.True(bitmap.Width > 0);
            Assert.True(bitmap.Height > 0);
            iconSignatures.Add(PixelSignature(bitmap));
        }

        Assert.Equal(Enum.GetValues<BrandIconVariant>().Length, iconSignatures.Count);

        foreach (SemanticColors colors in new[]
                 {
                     SemanticColors.Create(WindowsThemeKind.Light),
                     SemanticColors.Create(WindowsThemeKind.Dark),
                     SemanticColors.Create(WindowsThemeKind.HighContrast) with { Text = Color.Black },
                     SemanticColors.Create(WindowsThemeKind.HighContrast) with { Text = Color.White },
                 })
        {
            using Bitmap mark = FoxMouseIconFactory.CreateBrandMark(colors);
            Assert.Equal(1_254, mark.Width);
            Assert.Equal(1_254, mark.Height);
            Assert.Equal(0, mark.GetPixel(0, 0).A);
            Assert.Equal(0, mark.GetPixel(mark.Width - 1, mark.Height - 1).A);
        }
    }

    [Fact]
    public void SettingsThemeUrisUsePackagedBrandingPaths()
    {
        Assert.StartsWith("ms-appx:///Assets/Branding/", BrandLogoAssets.LightLogoUri);
        Assert.StartsWith("ms-appx:///Assets/Branding/", BrandLogoAssets.DarkLogoUri);
        Assert.StartsWith("ms-appx:///Assets/Branding/", BrandLogoAssets.HighContrastBlackLogoUri);
        Assert.StartsWith("ms-appx:///Assets/Branding/", BrandLogoAssets.HighContrastWhiteLogoUri);
        Assert.Equal(4, new HashSet<string>(
            [
                BrandLogoAssets.LightLogoUri,
                BrandLogoAssets.DarkLogoUri,
                BrandLogoAssets.HighContrastBlackLogoUri,
                BrandLogoAssets.HighContrastWhiteLogoUri,
            ], StringComparer.Ordinal).Count);
    }

    [Fact]
    public void GeneratedIcoFilesContainEveryRequiredThirtyTwoBitFrame()
    {
        string brandingRoot = FindBrandingRoot();
        foreach (string iconName in new[]
                 {
                     "FoxMouse.ico",
                     "FoxMouse.Dark.ico",
                     "FoxMouse.HighContrast.Black.ico",
                     "FoxMouse.HighContrast.White.ico",
                 })
        {
            string path = Path.Combine(brandingRoot, "generated", iconName);
            Assert.Equal(ExpectedIconSizes, ReadIcoFrames(path));
        }
    }

    [Fact]
    public void AppSettingsAndGuardExecutablesEmbedTheSameProductIcon()
    {
        string? expectedSignature = null;
        foreach (string executableName in new[]
                 {
                     "FoxMouse.exe",
                     "FoxMouse.Settings.exe",
                     "FoxMouse.Guard.exe",
                 })
        {
            string executablePath = Path.Combine(AppContext.BaseDirectory, executableName);
            Assert.True(File.Exists(executablePath), executablePath);
            using Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
            Assert.NotNull(icon);
            using Bitmap bitmap = icon.ToBitmap();
            string signature = PixelSignature(bitmap);
            expectedSignature ??= signature;
            Assert.Equal(expectedSignature, signature);
            Assert.Contains(EnumeratePixels(bitmap), static color => color.A == 0);
            Assert.Contains(EnumeratePixels(bitmap), static color => color.A == 255);
        }
    }

    [Fact]
    public void GeneratedPngFramesKeepTransparentEdgesAndMonochromeContract()
    {
        string generatedRoot = Path.Combine(FindBrandingRoot(), "generated", "icons");
        foreach (string theme in new[]
                 {
                     "light",
                     "dark",
                     "high-contrast-black",
                     "high-contrast-white",
                 })
        {
            foreach (int size in ExpectedIconSizes)
            {
                using Bitmap bitmap = new(Path.Combine(generatedRoot, theme, $"foxmouse-{size}.png"));
                Assert.Equal(size, bitmap.Width);
                Assert.Equal(size, bitmap.Height);
                Assert.Equal(0, bitmap.GetPixel(0, 0).A);
                Assert.Equal(0, bitmap.GetPixel(size - 1, 0).A);
                Assert.Equal(0, bitmap.GetPixel(0, size - 1).A);
                Assert.Equal(0, bitmap.GetPixel(size - 1, size - 1).A);
                Assert.Contains(EnumeratePixels(bitmap), static color => color.A == 255);

                if (theme.StartsWith("high-contrast", StringComparison.Ordinal))
                {
                    Assert.All(
                        EnumeratePixels(bitmap).Where(static color => color.A >= 240),
                        static color => Assert.True(color.R == color.G && color.G == color.B));
                }
            }
        }
    }

    [Fact]
    public void BrandingManifestCoversEveryAssetWithCurrentHashAndLength()
    {
        string brandingRoot = FindBrandingRoot();
        string manifestPath = Path.Combine(brandingRoot, "branding-manifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = document.RootElement;
        Assert.Equal("foxmouse.branding/1", root.GetProperty("schema").GetString());
        Assert.Equal("FoxMouse", root.GetProperty("product").GetString());
        Assert.Equal(ExpectedIconSizes, root.GetProperty("iconSizes").EnumerateArray().Select(static e => e.GetInt32()));

        JsonElement.ArrayEnumerator files = root.GetProperty("files").EnumerateArray();
        int count = 0;
        foreach (JsonElement record in files)
        {
            count++;
            string relativePath = record.GetProperty("path").GetString()!;
            string path = Path.GetFullPath(Path.Combine(brandingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(
                Path.GetFullPath(brandingRoot) + Path.DirectorySeparatorChar,
                path,
                StringComparison.OrdinalIgnoreCase);
            FileInfo file = new(path);
            Assert.True(file.Exists, relativePath);
            Assert.Equal(file.Length, record.GetProperty("bytes").GetInt64());
            Assert.Equal(
                record.GetProperty("sha256").GetString(),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        }

        Assert.True(count >= 50);
    }

    [Fact]
    public void RepeatedThemeIconReplacementDoesNotLeakGuiHandles()
    {
        using Process process = Process.GetCurrentProcess();
        for (int index = 0; index < 16; index++)
        {
            using Icon warmup = FoxMouseIconFactory.CreateApplicationIcon((BrandIconVariant)(index % 4));
            using Bitmap bitmap = warmup.ToBitmap();
        }

        ForceFullCollection();
        (uint gdi, uint user) before = CaptureGuiResources(process);
        for (int index = 0; index < 500; index++)
        {
            using Icon icon = FoxMouseIconFactory.CreateApplicationIcon((BrandIconVariant)(index % 4));
            using Bitmap bitmap = icon.ToBitmap();
        }

        ForceFullCollection();
        (uint gdi, uint user) after = CaptureGuiResources(process);
        Assert.True(after.gdi <= before.gdi + 2, $"GDI objects grew from {before.gdi} to {after.gdi}.");
        Assert.True(after.user <= before.user + 2, $"USER objects grew from {before.user} to {after.user}.");
    }

    private static IEnumerable<Color> EnumeratePixels(Bitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                yield return bitmap.GetPixel(x, y);
            }
        }
    }

    private static string PixelSignature(Bitmap bitmap)
    {
        using MemoryStream stream = new();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static int[] ReadIcoFrames(string path)
    {
        using BinaryReader reader = new(File.OpenRead(path));
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        int count = reader.ReadUInt16();
        List<int> sizes = new(count);
        long length = reader.BaseStream.Length;
        for (int index = 0; index < count; index++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            Assert.Equal((ushort)1, reader.ReadUInt16());
            Assert.Equal((ushort)32, reader.ReadUInt16());
            uint bytes = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            width = width == 0 ? 256 : width;
            height = height == 0 ? 256 : height;
            Assert.Equal(width, height);
            Assert.InRange((long)offset + bytes, 1, length);
            sizes.Add(width);
        }

        return sizes.ToArray();
    }

    private static string FindBrandingRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "assets", "branding");
            if (File.Exists(Path.Combine(candidate, "branding-manifest.json")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate assets/branding from the test output directory.");
    }

    private static (uint gdi, uint user) CaptureGuiResources(Process process)
    {
        process.Refresh();
        return (GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1));
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, int flags);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrandingAssetCollection
{
    public const string Name = "Branding asset tests";
}
