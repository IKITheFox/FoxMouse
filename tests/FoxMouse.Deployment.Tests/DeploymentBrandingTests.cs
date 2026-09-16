using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FoxMouse.Deployment;

namespace FoxMouse.Deployment.Tests;

[Collection(DeploymentBrandingCollection.Name)]
public sealed class DeploymentBrandingTests
{
    [Fact]
    public void ThemeMappingSelectsColorAndHighContrastVariants()
    {
        Assert.Equal(
            DeploymentBrandVariant.Light,
            DeploymentBranding.ResolveVariant(false, true, Color.Black));
        Assert.Equal(
            DeploymentBrandVariant.Dark,
            DeploymentBranding.ResolveVariant(false, false, Color.White));
        Assert.Equal(
            DeploymentBrandVariant.HighContrastBlack,
            DeploymentBranding.ResolveVariant(true, true, Color.Black));
        Assert.Equal(
            DeploymentBrandVariant.HighContrastWhite,
            DeploymentBranding.ResolveVariant(true, false, Color.White));
    }

    [Fact]
    public void EveryInstallerThemeResourceIsEmbeddedDistinctAndTransparent()
    {
        HashSet<string> iconSignatures = [];
        HashSet<string> markSignatures = [];
        foreach (DeploymentBrandVariant variant in Enum.GetValues<DeploymentBrandVariant>())
        {
            using Icon icon = DeploymentBranding.CreateIcon(variant);
            using Bitmap iconBitmap = icon.ToBitmap();
            Assert.True(iconBitmap.Width > 0);
            Assert.True(iconBitmap.Height > 0);
            iconSignatures.Add(Signature(iconBitmap));

            using Bitmap mark = DeploymentBranding.CreateMark(variant);
            Assert.Equal(1_254, mark.Width);
            Assert.Equal(1_254, mark.Height);
            Assert.Equal(0, mark.GetPixel(0, 0).A);
            Assert.Equal(0, mark.GetPixel(mark.Width - 1, mark.Height - 1).A);
            markSignatures.Add(Signature(mark));
        }

        Assert.Equal(Enum.GetValues<DeploymentBrandVariant>().Length, iconSignatures.Count);
        Assert.Equal(Enum.GetValues<DeploymentBrandVariant>().Length, markSignatures.Count);
    }

    [Fact]
    public void RepeatedInstallerThemeAssetReplacementDoesNotLeakGuiHandles()
    {
        using Process process = Process.GetCurrentProcess();
        for (int index = 0; index < 8; index++)
        {
            using Icon icon = DeploymentBranding.CreateIcon((DeploymentBrandVariant)(index % 4));
            using Bitmap mark = DeploymentBranding.CreateMark((DeploymentBrandVariant)(index % 4));
        }

        ForceFullCollection();
        (uint gdi, uint user) before = CaptureGuiResources(process);
        for (int index = 0; index < 64; index++)
        {
            using Icon icon = DeploymentBranding.CreateIcon((DeploymentBrandVariant)(index % 4));
            using Bitmap mark = DeploymentBranding.CreateMark((DeploymentBrandVariant)(index % 4));
        }

        ForceFullCollection();
        (uint gdi, uint user) after = CaptureGuiResources(process);
        Assert.True(after.gdi <= before.gdi + 2, $"GDI objects grew from {before.gdi} to {after.gdi}.");
        Assert.True(after.user <= before.user + 2, $"USER objects grew from {before.user} to {after.user}.");
    }

    private static string Signature(Bitmap bitmap)
    {
        using MemoryStream stream = new();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
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
public sealed class DeploymentBrandingCollection
{
    public const string Name = "Deployment branding tests";
}
