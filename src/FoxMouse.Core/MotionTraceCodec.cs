using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxMouse.Core;

public static class MotionTraceCodec
{
    public const int MaximumSamples = 1_000_000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static MotionTrace Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = File.OpenText(path);
        return Read(reader);
    }

    public static MotionTrace Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        MotionTraceHeader? header = null;
        var samples = new List<MotionSample>();
        var lineNumber = 0;
        var lastTimestamp = -1L;
        long? endTimestamp = null;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (endTimestamp.HasValue)
            {
                throw Error(lineNumber, "No records are allowed after the end record.");
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("kind", out var kindElement)
                    || kindElement.ValueKind != JsonValueKind.String)
                {
                    throw Error(lineNumber, "Every record must be an object with a string 'kind'.");
                }

                var kind = kindElement.GetString();
                switch (kind)
                {
                    case "header":
                        if (header is not null || samples.Count > 0)
                        {
                            throw Error(lineNumber, "The header must be the first and only header record.");
                        }

                        header = JsonSerializer.Deserialize<MotionTraceHeader>(line, SerializerOptions)
                            ?? throw Error(lineNumber, "The header is null.");
                        ValidateHeader(header, lineNumber);
                        break;

                    case "sample":
                        if (header is null)
                        {
                            throw Error(lineNumber, "A sample cannot appear before the header.");
                        }

                        if (samples.Count >= MaximumSamples)
                        {
                            throw Error(lineNumber, $"A trace may contain at most {MaximumSamples} samples.");
                        }

                        var dto = JsonSerializer.Deserialize<SampleRecord>(line, SerializerOptions)
                            ?? throw Error(lineNumber, "The sample is null.");
                        var sample = dto.ToMotionSample();
                        ValidateSample(sample, lastTimestamp, lineNumber);
                        samples.Add(sample);
                        lastTimestamp = sample.TimestampMicroseconds;
                        break;

                    case "end":
                        if (header is null)
                        {
                            throw Error(lineNumber, "The end record cannot appear before the header.");
                        }

                        var end = JsonSerializer.Deserialize<EndRecord>(line, SerializerOptions)
                            ?? throw Error(lineNumber, "The end record is null.");
                        if (end.SampleCount != samples.Count)
                        {
                            throw Error(
                                lineNumber,
                                $"End sample_count is {end.SampleCount}, but {samples.Count} samples were read.");
                        }

                        if (end.TimestampMicroseconds < 0 || end.TimestampMicroseconds < lastTimestamp)
                        {
                            throw Error(lineNumber, "End t_us must be non-negative and not precede the last sample.");
                        }

                        endTimestamp = end.TimestampMicroseconds;
                        break;

                    default:
                        throw Error(lineNumber, $"Unknown record kind '{kind}'.");
                }
            }
            catch (MotionTraceFormatException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or OverflowException)
            {
                throw new MotionTraceFormatException(
                    string.Create(CultureInfo.InvariantCulture, $"Line {lineNumber}: invalid JSON record."),
                    exception);
            }
        }

        if (header is null)
        {
            throw new MotionTraceFormatException("The trace has no header record.");
        }

        if (!endTimestamp.HasValue)
        {
            throw new MotionTraceFormatException("The trace has no end record.");
        }

        return new MotionTrace(header, samples, endTimestamp.Value);
    }

    public static void Write(string path, MotionTrace trace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(trace);
        using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        Write(writer, trace);
    }

    public static void Write(TextWriter writer, MotionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(trace);
        ValidateHeader(trace.Header, 1);
        if (trace.Samples.Count > MaximumSamples)
        {
            throw new MotionTraceFormatException(
                $"A trace may contain at most {MaximumSamples} samples.");
        }

        writer.WriteLine(JsonSerializer.Serialize(trace.Header, SerializerOptions));
        var lastTimestamp = -1L;
        var lineNumber = 1;
        foreach (var sample in trace.Samples)
        {
            lineNumber++;
            ValidateSample(sample, lastTimestamp, lineNumber);
            writer.WriteLine(JsonSerializer.Serialize(SampleRecord.From(sample), SerializerOptions));
            lastTimestamp = sample.TimestampMicroseconds;
        }

        if (trace.EndTimestampMicroseconds < 0
            || trace.EndTimestampMicroseconds < lastTimestamp)
        {
            throw new MotionTraceFormatException(
                "End t_us must be non-negative and not precede the last sample.");
        }

        writer.WriteLine(JsonSerializer.Serialize(
            new EndRecord("end", trace.EndTimestampMicroseconds, trace.Samples.Count),
            SerializerOptions));
    }

    private static void ValidateHeader(MotionTraceHeader header, int lineNumber)
    {
        if (header.Kind != "header"
            || header.Schema != MotionTraceHeader.CurrentSchema
            || header.Algorithm != MotionTraceHeader.CurrentAlgorithm
            || header.TimeUnit != "us"
            || header.DistanceUnit != "milli_dip"
            || !header.Normalized)
        {
            throw Error(lineNumber, "Unsupported or non-normalized motion trace header.");
        }
    }

    private static void ValidateSample(MotionSample sample, long lastTimestamp, int lineNumber)
    {
        if (sample.TimestampMicroseconds < 0 || sample.TimestampMicroseconds <= lastTimestamp)
        {
            throw Error(lineNumber, "Sample timestamps must be non-negative and strictly increasing.");
        }

        if (sample.DeviceId < 0)
        {
            throw Error(lineNumber, "device must be non-negative.");
        }

        const PointerButtons knownButtons = PointerButtons.Left
            | PointerButtons.Right
            | PointerButtons.Middle
            | PointerButtons.XButton1
            | PointerButtons.XButton2;
        if ((sample.Buttons & ~knownButtons) != 0)
        {
            throw Error(lineNumber, "buttons contains unknown bits.");
        }

        const MotionSampleFlags knownFlags = MotionSampleFlags.EdgeEstimated
            | MotionSampleFlags.Discontinuity
            | MotionSampleFlags.DeviceChanged;
        if ((sample.Flags & ~knownFlags) != 0)
        {
            throw Error(lineNumber, "flags contains unknown bits.");
        }
    }

    private static MotionTraceFormatException Error(int lineNumber, string message) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Line {lineNumber}: {message}"));

    private sealed record SampleRecord(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("t_us")] long TimestampMicroseconds,
        [property: JsonPropertyName("device")] int DeviceId,
        [property: JsonPropertyName("dx_mdip")] int DeltaXMilliDip,
        [property: JsonPropertyName("dy_mdip")] int DeltaYMilliDip,
        [property: JsonPropertyName("buttons")] int Buttons,
        [property: JsonPropertyName("flags")] int Flags)
    {
        public MotionSample ToMotionSample()
        {
            if (Kind != "sample")
            {
                throw new MotionTraceFormatException("A sample record has an invalid kind.");
            }

            return new MotionSample(
                TimestampMicroseconds,
                DeviceId,
                DeltaXMilliDip,
                DeltaYMilliDip,
                (PointerButtons)Buttons,
                (MotionSampleFlags)Flags);
        }

        public static SampleRecord From(MotionSample sample) => new(
            "sample",
            sample.TimestampMicroseconds,
            sample.DeviceId,
            sample.DeltaXMilliDip,
            sample.DeltaYMilliDip,
            (int)sample.Buttons,
            (int)sample.Flags);
    }

    private sealed record EndRecord(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("t_us")] long TimestampMicroseconds,
        [property: JsonPropertyName("sample_count")] int SampleCount);
}
