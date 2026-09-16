using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class ShakeDetectorPropertyTests
{
    [Fact]
    public void SampleRatesProduceEquivalentTriggers()
    {
        var rates = new[] { 125, 1_000, 8_000 };
        var results = rates.Select(rate => MotionTestData.Run(MotionTestData.SineWave(rate))).ToArray();

        Assert.All(results, static result => Assert.NotNull(result.Trigger));
        var triggerTimes = results.Select(static result => result.Trigger!.Value).ToArray();
        Assert.True(triggerTimes.Max() - triggerTimes.Min() <= 20_000);
        var scores = results.Select(static result => result.MaximumScore).ToArray();
        Assert.True(scores.Max() - scores.Min() <= 0.05);
    }

    [Fact]
    public void MirroringAndQuarterRotationPreserveDetection()
    {
        var original = MotionTestData.Alternating();
        var mirrored = original.Select(static sample => sample with
        {
            DeltaXMilliDip = -sample.DeltaXMilliDip,
            DeltaYMilliDip = -sample.DeltaYMilliDip,
        });
        var rotated = original.Select(static sample => sample with
        {
            DeltaXMilliDip = -sample.DeltaYMilliDip,
            DeltaYMilliDip = sample.DeltaXMilliDip,
        });

        var originalResult = MotionTestData.Run(original);
        var mirroredResult = MotionTestData.Run(mirrored);
        var rotatedResult = MotionTestData.Run(rotated);

        Assert.Equal(originalResult.Trigger, mirroredResult.Trigger);
        Assert.Equal(originalResult.Trigger, rotatedResult.Trigger);
        Assert.Equal(originalResult.MaximumScore, mirroredResult.MaximumScore, 10);
        Assert.Equal(originalResult.MaximumScore, rotatedResult.MaximumScore, 10);
    }

    [Fact]
    public void SplittingPacketsPreservesOutcomeWithinOneOriginalPacket()
    {
        var original = MotionTestData.Alternating();
        var split = new List<MotionSample>();
        foreach (var sample in original)
        {
            split.Add(sample with
            {
                TimestampMicroseconds = sample.TimestampMicroseconds - 4_000,
                DeltaXMilliDip = sample.DeltaXMilliDip / 2,
                DeltaYMilliDip = sample.DeltaYMilliDip / 2,
            });
            split.Add(sample with
            {
                DeltaXMilliDip = sample.DeltaXMilliDip - (sample.DeltaXMilliDip / 2),
                DeltaYMilliDip = sample.DeltaYMilliDip - (sample.DeltaYMilliDip / 2),
            });
        }

        var originalResult = MotionTestData.Run(original);
        var splitResult = MotionTestData.Run(split);

        Assert.NotNull(originalResult.Trigger);
        Assert.NotNull(splitResult.Trigger);
        Assert.True(Math.Abs(originalResult.Trigger.Value - splitResult.Trigger.Value) <= 8_000);
    }

    [Fact]
    public void RandomInputAlwaysProducesFiniteBoundedScoreAndBoundedWindow()
    {
        const int seeds = 100;
        const int samplesPerSeed = 500;

        for (var seed = 0; seed < seeds; seed++)
        {
            var random = new Random(seed);
            var options = ShakeDetectorOptions.Default with { MaximumSamplesPerDevice = 64 };
            var detector = new ShakeDetector(options);
            var timestamp = 0L;
            for (var index = 0; index < samplesPerSeed; index++)
            {
                timestamp += random.Next(1_000, 20_000);
                var result = detector.Process(new MotionSample(
                    timestamp,
                    random.Next(0, 3),
                    random.Next(-20_000, 20_001),
                    random.Next(-20_000, 20_001)));

                Assert.True(double.IsFinite(result.Score));
                Assert.InRange(result.Score, 0.0, 1.0);
                Assert.InRange(result.Metrics.SampleCount, 0, options.MaximumSamplesPerDevice);
            }
        }
    }

    [Fact]
    public void ReplayingSameTraceIsDeterministic()
    {
        var trace = MotionTestData.SineWave(1_000).ToArray();
        var expected = MotionTestData.Run(trace);

        for (var iteration = 0; iteration < 25; iteration++)
        {
            Assert.Equal(expected, MotionTestData.Run(trace));
        }
    }

    [Fact]
    public void EightKilohertzInputRetainsBoundedAnalysisWindow()
    {
        var detector = new ShakeDetector();
        int maximumSamples = 0;
        bool becameActive = false;

        foreach (MotionSample sample in MotionTestData.SineWave(
                     sampleRate: 8_000,
                     durationMicroseconds: 2_000_000))
        {
            ShakeDetectionResult result = detector.Process(sample);
            maximumSamples = Math.Max(maximumSamples, result.Metrics.SampleCount);
            becameActive |= result.BecameActive;
        }

        int expectedUpperBound =
            (int)(detector.Options.WindowMicroseconds /
                  detector.Options.AnalysisIntervalMicroseconds) + 2;
        Assert.True(becameActive);
        Assert.InRange(maximumSamples, 1, expectedUpperBound);
    }

    [Fact]
    public void CurrentActivityAggregatesConcurrentDevicesWithoutLosingScore()
    {
        var detector = new ShakeDetector();
        foreach (MotionSample sample in MotionTestData.Alternating(deviceId: 0))
        {
            _ = detector.Process(sample);
        }

        foreach (MotionSample sample in MotionTestData.Alternating(
                     deviceId: 1,
                     startTimestampMicroseconds: 1_000))
        {
            _ = detector.Process(sample);
        }

        ShakeActivitySnapshot activity = detector.CurrentActivity;

        Assert.Equal(2, activity.TrackedDeviceCount);
        Assert.Equal(2, activity.ActiveDeviceCount);
        Assert.True(activity.IsActive);
        Assert.InRange(activity.MaximumActiveScore, 0.85, 1.0);
        Assert.Equal(2, activity.DeviceResults.Count);
    }

    [Fact]
    public void SteadyStateEightKilohertzProcessingIsAllocationFree()
    {
        MotionSample[] warmup = MotionTestData.SineWave(
                sampleRate: 8_000,
                durationMicroseconds: 500_000)
            .ToArray();
        MotionSample[] measured = MotionTestData.SineWave(
                sampleRate: 8_000,
                durationMicroseconds: 500_000)
            .Select(static sample => sample with
            {
                TimestampMicroseconds = sample.TimestampMicroseconds + 500_000,
            })
            .ToArray();
        var detector = new ShakeDetector();
        foreach (MotionSample sample in warmup)
        {
            _ = detector.Process(sample);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (MotionSample sample in measured)
        {
            _ = detector.Process(sample);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
