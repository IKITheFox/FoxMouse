using System.IO.Pipes;
using FoxMouse.Settings;
using FoxMouse.Platform.Windows.Configuration;

namespace FoxMouse.Settings.Tests;

public sealed class SettingsActivationRequestTests
{
    [Theory]
    [InlineData(null, SettingsPageKind.General)]
    [InlineData("", SettingsPageKind.General)]
    [InlineData("--page general", SettingsPageKind.General)]
    [InlineData("--page=exclusions", SettingsPageKind.Exclusions)]
    [InlineData("--page processes", SettingsPageKind.Exclusions)]
    [InlineData("--page about", SettingsPageKind.About)]
    [InlineData("--page unknown", SettingsPageKind.General)]
    public void ParseSelectsOnlyKnownPages(string? arguments, SettingsPageKind expected)
    {
        Assert.Equal(expected, SettingsActivationRequest.Parse(arguments).Page);
    }

    [Fact]
    public void ParseCarriesOnlyAValidatedLaunchToken()
    {
        string token = SettingsHostReadyChannel.CreateToken();

        SettingsActivationRequest valid = SettingsActivationRequest.Parse(
            $"--launch-token {token} --page about");
        SettingsActivationRequest invalid = SettingsActivationRequest.Parse(
            "--page exclusions --launch-token ..\\unsafe");

        Assert.Equal(SettingsPageKind.About, valid.Page);
        Assert.Equal(token, valid.LaunchToken);
        Assert.Equal(SettingsPageKind.Exclusions, invalid.Page);
        Assert.Null(invalid.LaunchToken);
    }

    [Fact]
    public void ActivationPayloadRoundTripsPageAndAcknowledgementToken()
    {
        string token = SettingsHostReadyChannel.CreateToken();

        bool parsed = SettingsActivationChannel.TryParsePayload(
            $"About|{token}",
            out SettingsActivationRequest? request);

        Assert.True(parsed);
        Assert.Equal(SettingsPageKind.About, request?.Page);
        Assert.Equal(token, request?.LaunchToken);
        Assert.False(SettingsActivationChannel.TryParsePayload("About|invalid", out _));
        Assert.False(SettingsActivationChannel.TryParsePayload("Unknown", out _));
    }

    [Fact]
    public async Task OccupiedActivationPipeYieldsInsteadOfBlockingApplicationStartup()
    {
        string pipeName = $"FoxMouse.Settings.Tests.Occupied.{Guid.NewGuid():N}";
        await using NamedPipeServerStream occupiedPipe = new(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim listenerReturned = new(initialState: false);
        Task? listener = null;
        Thread thread = new(() =>
        {
            listener = SettingsActivationChannel.ListenAsync(_ => { }, cancellation.Token, pipeName);
            listenerReturned.Set();
        })
        {
            IsBackground = true,
            Name = "FoxMouse occupied activation pipe test",
        };

        thread.Start();
        try
        {
            Assert.True(
                listenerReturned.Wait(TimeSpan.FromSeconds(1)),
                "The async activation listener blocked synchronously while the pipe was occupied.");
        }
        finally
        {
            cancellation.Cancel();
            Assert.True(thread.Join(TimeSpan.FromSeconds(1)));
        }

        Assert.NotNull(listener);
        await listener!;
    }
}
