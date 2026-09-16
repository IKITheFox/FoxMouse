using FoxMouse.Core;

internal sealed record TraceFixture(
    string Name,
    MotionTrace Trace,
    TraceExpectation Expectation);

internal static class BuiltInTraceCorpus
{
    public static IReadOnlyList<TraceFixture> Create() =>
    [
        Positive("horizontal-fast", 12_000, 0),
        Positive("vertical-fast", 0, 12_000),
        Positive("diagonal-fast", 8_500, 8_500),
        Negative("straight-fling", StraightFling(), 0.45, 0.65),
        Negative("micro-jitter", MicroJitter(), 0.0, 0.60),
        Negative("drag-shake", Alternating(12_000, 0, PointerButtons.Left), 0.0, 0.0),
    ];

    private static TraceFixture Positive(string name, int deltaX, int deltaY)
    {
        var samples = Alternating(deltaX, deltaY);
        var end = samples[^1].TimestampMicroseconds + 500_000;
        return new TraceFixture(
            name,
            CreateTrace(samples, end),
            new TraceExpectation
            {
                TriggerCount = 1,
                FirstTriggerMicroseconds = new LongRangeExpectation
                {
                    Minimum = 240_000,
                    Maximum = 360_000,
                },
                ReleaseByMicroseconds = end,
                MaximumScore = new DoubleRangeExpectation { Minimum = 0.85, Maximum = 1.0 },
            });
    }

    private static TraceFixture Negative(
        string name,
        IReadOnlyList<MotionSample> samples,
        double minimumScore,
        double maximumScore)
    {
        var end = samples[^1].TimestampMicroseconds + 500_000;
        return new TraceFixture(
            name,
            CreateTrace(samples, end),
            new TraceExpectation
            {
                TriggerCount = 0,
                MaximumScore = new DoubleRangeExpectation
                {
                    Minimum = minimumScore,
                    Maximum = maximumScore,
                },
            });
    }

    private static MotionTrace CreateTrace(IReadOnlyList<MotionSample> samples, long end) =>
        new(
            new MotionTraceHeader { Generator = "FoxMouse.TraceTool", Seed = 42 },
            samples,
            end);

    private static IReadOnlyList<MotionSample> Alternating(
        int deltaX,
        int deltaY,
        PointerButtons buttons = PointerButtons.None)
    {
        var samples = new List<MotionSample>();
        var timestamp = 0L;
        for (var stroke = 0; stroke < 4; stroke++)
        {
            var sign = stroke % 2 == 0 ? 1 : -1;
            for (var index = 0; index < 10; index++)
            {
                timestamp += 8_000;
                samples.Add(new MotionSample(
                    timestamp,
                    0,
                    sign * deltaX,
                    sign * deltaY,
                    buttons));
            }
        }

        return samples;
    }

    private static IReadOnlyList<MotionSample> StraightFling()
    {
        var samples = new List<MotionSample>();
        for (var index = 1; index <= 30; index++)
        {
            samples.Add(new MotionSample(index * 8_000L, 0, 12_000, 0));
        }

        return samples;
    }

    private static IReadOnlyList<MotionSample> MicroJitter()
    {
        var samples = new List<MotionSample>();
        for (var index = 1; index <= 50; index++)
        {
            var sign = index % 2 == 0 ? 1 : -1;
            samples.Add(new MotionSample(index * 8_000L, 0, sign * 1_500, 0));
        }

        return samples;
    }
}
