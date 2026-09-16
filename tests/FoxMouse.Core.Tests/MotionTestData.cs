using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

internal static class MotionTestData
{
    public static IReadOnlyList<MotionSample> Alternating(
        int deltaX = 12_000,
        int deltaY = 0,
        int strokes = 4,
        int samplesPerStroke = 10,
        long intervalMicroseconds = 8_000,
        int deviceId = 0,
        PointerButtons buttons = PointerButtons.None,
        long startTimestampMicroseconds = 0)
    {
        var samples = new List<MotionSample>();
        var timestamp = startTimestampMicroseconds;
        for (var stroke = 0; stroke < strokes; stroke++)
        {
            var sign = stroke % 2 == 0 ? 1 : -1;
            for (var index = 0; index < samplesPerStroke; index++)
            {
                timestamp += intervalMicroseconds;
                samples.Add(new MotionSample(
                    timestamp,
                    deviceId,
                    sign * deltaX,
                    sign * deltaY,
                    buttons));
            }
        }

        return samples;
    }

    public static IReadOnlyList<MotionSample> SineWave(
        int sampleRate,
        double amplitudeDip = 70,
        double frequencyHz = 6,
        long durationMicroseconds = 500_000)
    {
        var samples = new List<MotionSample>();
        var interval = MotionSample.MicrosecondsPerSecond / sampleRate;
        var previousX = 0.0;
        for (var timestamp = interval; timestamp <= durationMicroseconds; timestamp += interval)
        {
            var seconds = (double)timestamp / MotionSample.MicrosecondsPerSecond;
            var x = amplitudeDip * Math.Sin(2.0 * Math.PI * frequencyHz * seconds);
            var delta = (int)Math.Round((x - previousX) * MotionSample.MilliDipPerDip);
            samples.Add(new MotionSample(timestamp, 0, delta, 0));
            previousX = x;
        }

        return samples;
    }

    public static (long? Trigger, double MaximumScore) Run(
        IEnumerable<MotionSample> samples,
        ShakeDetectorOptions? options = null)
    {
        var detector = new ShakeDetector(options);
        long? trigger = null;
        var maximumScore = 0.0;
        foreach (var sample in samples)
        {
            var result = detector.Process(sample);
            maximumScore = Math.Max(maximumScore, result.Score);
            if (result.BecameActive)
            {
                trigger ??= result.TimestampMicroseconds;
            }
        }

        return (trigger, maximumScore);
    }
}
