using FoxMouse.Core;
using FoxMouse.Platform.Windows.Input;

namespace FoxMouse.App;

internal static class MotionTraceRecorder
{
    internal static int Run(string outputPath, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            return 2;
        }

        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<MotionSample> samples = [];
        InputNormalizer normalizer = new();
        using RawInputSink input = new();
        input.PacketReceived += (_, packet) =>
        {
            MotionSample sample = normalizer.Normalize(packet);
            if (samples.Count == 0 || sample.TimestampMicroseconds > samples[^1].TimestampMicroseconds)
            {
                samples.Add(sample);
            }
        };

        long start = input.CurrentTimestampMicroseconds;
        long durationMicroseconds = (long)(duration.TotalMilliseconds * 1_000);
        while (input.CurrentTimestampMicroseconds - start < durationMicroseconds)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }

        long end = Math.Max(
            input.CurrentTimestampMicroseconds,
            samples.Count == 0 ? 0 : samples[^1].TimestampMicroseconds);
        MotionTrace trace = new(
            new MotionTraceHeader
            {
                Generator = "FoxMouse.App real-input recorder",
            },
            samples,
            end);
        MotionTraceCodec.Write(fullPath, trace);
        return 0;
    }
}
