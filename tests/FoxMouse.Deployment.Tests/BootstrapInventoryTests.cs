using FoxMouse.Bootstrap;

namespace FoxMouse.Deployment.Tests;

public sealed class BootstrapInventoryTests
{
    [Theory]
    [InlineData("Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsAppRuntime.2_2.4.1.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsAppRuntime.2_2.3.0.0_x64__8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.WindowsAppRuntime.2_2.4.0.0_x86__8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__wrongpublisher", false)]
    [InlineData("Microsoft.WindowsAppRuntime.2_bad_x64__8wekyb3d8bbwe", false)]
    [InlineData("broken", false)]
    public void PackageDetectionRequiresFamilyArchitectureAndMinimumVersion(string fullName, bool expected) =>
        Assert.Equal(expected, BootstrapInventory.PackageMatches(fullName,
            "Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe", new Version(2, 4, 0, 0), "x64"));

    [Fact]
    public void UnknownPackageIsNotReportedInstalled() =>
        Assert.False(BootstrapInventory.HasRegisteredPackage("FoxMouse.Nonexistent_8wekyb3d8bbwe", new Version(1, 0), "x64"));
}
