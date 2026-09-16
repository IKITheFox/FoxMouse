using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxMouse.Core;

public sealed record LongRangeExpectation
{
    [JsonPropertyName("min")]
    public long Minimum { get; init; }

    [JsonPropertyName("max")]
    public long Maximum { get; init; }
}

public sealed record DoubleRangeExpectation
{
    [JsonPropertyName("min")]
    public double Minimum { get; init; }

    [JsonPropertyName("max")]
    public double Maximum { get; init; }
}

public sealed record TraceExpectation
{
    public const string CurrentSchema = "foxmouse.expect/1";

    [JsonPropertyName("schema")]
    public string Schema { get; init; } = CurrentSchema;

    [JsonPropertyName("config")]
    public string Config { get; init; } = "default-v1";

    [JsonPropertyName("trigger_count")]
    public int TriggerCount { get; init; }

    [JsonPropertyName("first_trigger_us")]
    public LongRangeExpectation? FirstTriggerMicroseconds { get; init; }

    [JsonPropertyName("release_by_us")]
    public long? ReleaseByMicroseconds { get; init; }

    [JsonPropertyName("max_score")]
    public DoubleRangeExpectation? MaximumScore { get; init; }

    [JsonPropertyName("never_active_while_buttons_nonzero")]
    public bool NeverActiveWhileButtonsNonzero { get; init; } = true;
}

public static class TraceExpectationCodec
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static TraceExpectation Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var expectation = JsonSerializer.Deserialize<TraceExpectation>(File.ReadAllText(path), Options)
            ?? throw new MotionTraceFormatException("The trace expectation is null.");
        Validate(expectation);
        return expectation;
    }

    public static void Write(string path, TraceExpectation expectation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate(expectation);
        File.WriteAllText(path, JsonSerializer.Serialize(expectation, Options));
    }

    public static void Validate(TraceExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        if (expectation.Schema != TraceExpectation.CurrentSchema)
        {
            throw new MotionTraceFormatException($"Unsupported expectation schema '{expectation.Schema}'.");
        }

        if (expectation.Config != "default-v1")
        {
            throw new MotionTraceFormatException($"Unsupported detector config '{expectation.Config}'.");
        }

        if (expectation.TriggerCount < 0)
        {
            throw new MotionTraceFormatException("trigger_count must be non-negative.");
        }

        if (expectation.FirstTriggerMicroseconds is { } triggerRange
            && (triggerRange.Minimum < 0 || triggerRange.Maximum < triggerRange.Minimum))
        {
            throw new MotionTraceFormatException("first_trigger_us is invalid.");
        }

        if (expectation.ReleaseByMicroseconds < 0)
        {
            throw new MotionTraceFormatException("release_by_us must be non-negative.");
        }

        if (expectation.MaximumScore is { } scoreRange
            && (!double.IsFinite(scoreRange.Minimum)
                || !double.IsFinite(scoreRange.Maximum)
                || scoreRange.Minimum < 0
                || scoreRange.Maximum > 1
                || scoreRange.Maximum < scoreRange.Minimum))
        {
            throw new MotionTraceFormatException("max_score is invalid.");
        }
    }
}
