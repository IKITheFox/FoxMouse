using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxMouse.Platform.Windows.Visibility;

public static class GuardProtocol
{
    public const int Version = 1;
    public const int DefaultLeaseMilliseconds = 1_200;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SerializeRequest(GuardRequest request) => JsonSerializer.Serialize(request, Options);

    public static string SerializeResponse(GuardResponse response) => JsonSerializer.Serialize(response, Options);

    public static GuardRequest? ParseRequest(string line) => JsonSerializer.Deserialize<GuardRequest>(line, Options);

    public static GuardResponse? ParseResponse(string line) => JsonSerializer.Deserialize<GuardResponse>(line, Options);
}

public sealed record GuardRequest(
    string Operation,
    long Generation,
    int LeaseMilliseconds = GuardProtocol.DefaultLeaseMilliseconds,
    int ProtocolVersion = GuardProtocol.Version);

public sealed record GuardResponse(
    string Operation,
    long Generation,
    bool Success,
    string? Error = null,
    int ProtocolVersion = GuardProtocol.Version);
