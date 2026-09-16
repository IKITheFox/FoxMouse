using System.Text.Json;
using FoxMouse.Platform.Windows.Visibility;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class GuardProtocolTests
{
    [Fact]
    public void RequestRoundTripPreservesEveryFieldAndUsesSnakeCase()
    {
        GuardRequest expected = new(
            Operation: "renew",
            Generation: 42,
            LeaseMilliseconds: 1_250,
            ProtocolVersion: GuardProtocol.Version);

        string json = GuardProtocol.SerializeRequest(expected);
        GuardRequest? actual = GuardProtocol.ParseRequest(json);

        Assert.Equal(expected, actual);
        Assert.Contains("\"lease_milliseconds\":1250", json, StringComparison.Ordinal);
        Assert.Contains("\"protocol_version\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseRoundTripPreservesError()
    {
        GuardResponse expected = new(
            Operation: "arm",
            Generation: long.MaxValue,
            Success: false,
            Error: "lease rejected",
            ProtocolVersion: GuardProtocol.Version);

        string json = GuardProtocol.SerializeResponse(expected);
        GuardResponse? actual = GuardProtocol.ParseResponse(json);

        Assert.Equal(expected, actual);
        Assert.Contains("\"success\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"error\":\"lease rejected\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NullErrorIsOmittedAndDefaultValuesSurviveRoundTrip()
    {
        GuardResponse expected = new("disarm", 7, true);

        string json = GuardProtocol.SerializeResponse(expected);
        GuardResponse? actual = GuardProtocol.ParseResponse(json);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("\"error\"", json, StringComparison.Ordinal);
        Assert.Equal(GuardProtocol.Version, actual?.ProtocolVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"not-an-object\"")]
    public void MalformedRequestThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => GuardProtocol.ParseRequest(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("42")]
    public void MalformedResponseThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => GuardProtocol.ParseResponse(json));
    }

    [Fact]
    public void JsonNullProducesNoMessage()
    {
        Assert.Null(GuardProtocol.ParseRequest("null"));
        Assert.Null(GuardProtocol.ParseResponse("null"));
    }
}
