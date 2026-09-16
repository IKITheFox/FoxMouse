using FoxMouse.Bootstrap;
using System.Security.Cryptography;

namespace FoxMouse.Deployment.Tests;

public sealed class BootstrapDependencyPolicyTests
{
    [Theory]
    [InlineData(3010, true)]
    [InlineData(3010, false)]
    [InlineData(1641, true)]
    [InlineData(1641, false)]
    public void RestartIsDistinctFromRetryableFailureEvenWhenDetectionSucceeds(int code, bool detected)
    {
        BootstrapRestartRequiredException error = Assert.Throws<BootstrapRestartRequiredException>(
            () => BootstrapDependencyPolicy.VerifyInstallationResult(code, detected));
        Assert.Equal(code, error.ExitCode);
    }

    [Theory]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/runtime.exe", true)]
    [InlineData("https://download.microsoft.com/runtime.exe", true)]
    [InlineData("http://download.microsoft.com/runtime.exe", false)]
    [InlineData("https://download.microsoft.com.evil.test/runtime.exe", false)]
    [InlineData("https://download.microsoft.com:444/runtime.exe", false)]
    [InlineData("https://user@download.microsoft.com/runtime.exe", false)]
    [InlineData("file:///C:/runtime.exe", false)]
    public void OnlyApprovedHttpsOriginsAreAccepted(string url, bool expected)
    {
        Exception? error = Record.Exception(() => BootstrapDependencyPolicy.ValidateDownloadUri(new Uri(url)));
        Assert.Equal(expected, error is null);
    }

    [Theory]
    [InlineData("10.0.11", true)]
    [InlineData("10.0.12", true)]
    [InlineData("10.0.10", false)]
    [InlineData("11.0.0", false)]
    [InlineData("10.1.0", false)]
    [InlineData("10.0.11-preview", false)]
    public void RuntimeCompatibilityDoesNotAssumeMajorOrMinorRollForward(string installed, bool expected) =>
        Assert.Equal(expected, BootstrapDependencyPolicy.IsCompatibleRuntime(installed, "10.0.11"));

    [Fact]
    public void ModifiedOrUnpinnedPayloadCannotPassVerification()
    {
        byte[] bytes = [1, 2, 3];
        string digest = Convert.ToHexString(SHA512.HashData(bytes));
        BootstrapDependencyPolicy.VerifySha512(new MemoryStream(bytes), digest);
        Assert.Throws<InvalidDataException>(() => BootstrapDependencyPolicy.VerifySha512(new MemoryStream([1, 2, 4]), digest));
        Assert.Throws<InvalidDataException>(() => BootstrapDependencyPolicy.VerifySha512(new MemoryStream(bytes), ""));
        Assert.Throws<InvalidDataException>(() => BootstrapDependencyPolicy.VerifySha512(new MemoryStream(bytes), new string('z', 128)));
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(0, false, false)]
    [InlineData(1603, true, false)]
    [InlineData(3010, true, false)]
    [InlineData(1641, true, false)]
    public void SuccessfulExitRequiresFreshDetectionAndNoPendingRestart(int exitCode, bool detected, bool expected)
    {
        Exception? error = Record.Exception(() => BootstrapDependencyPolicy.VerifyInstallationResult(exitCode, detected));
        Assert.Equal(expected, error is null);
    }
}
