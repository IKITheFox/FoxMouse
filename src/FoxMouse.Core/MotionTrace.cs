using System.Text.Json.Serialization;

namespace FoxMouse.Core;

public sealed record MotionTraceHeader
{
    public const string CurrentSchema = "foxmouse.motion/1";
    public const string CurrentAlgorithm = "shake-v1";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "header";

    [JsonPropertyName("schema")]
    public string Schema { get; init; } = CurrentSchema;

    [JsonPropertyName("algorithm")]
    public string Algorithm { get; init; } = CurrentAlgorithm;

    [JsonPropertyName("time_unit")]
    public string TimeUnit { get; init; } = "us";

    [JsonPropertyName("distance_unit")]
    public string DistanceUnit { get; init; } = "milli_dip";

    [JsonPropertyName("normalized")]
    public bool Normalized { get; init; } = true;

    [JsonPropertyName("generator")]
    public string? Generator { get; init; }

    [JsonPropertyName("seed")]
    public int? Seed { get; init; }
}

public sealed class MotionTrace
{
    public MotionTrace(
        MotionTraceHeader header,
        IEnumerable<MotionSample> samples,
        long endTimestampMicroseconds)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Samples = samples?.ToArray() ?? throw new ArgumentNullException(nameof(samples));
        EndTimestampMicroseconds = endTimestampMicroseconds;
    }

    public MotionTraceHeader Header { get; }

    public IReadOnlyList<MotionSample> Samples { get; }

    public long EndTimestampMicroseconds { get; }
}

public sealed class MotionTraceFormatException : FormatException
{
    public MotionTraceFormatException(string message)
        : base(message)
    {
    }

    public MotionTraceFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
