using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class ShakeDetectorTests
{
    [Theory]
    [InlineData(12_000, 0)]
    [InlineData(0, 12_000)]
    [InlineData(8_500, 8_500)]
    [InlineData(8_500, -8_500)]
    public void FastAlternatingMotionTriggersInAnyAxis(int deltaX, int deltaY)
    {
        var (trigger, score) = MotionTestData.Run(MotionTestData.Alternating(deltaX, deltaY));

        Assert.NotNull(trigger);
        Assert.InRange(trigger.Value, 240_000, 360_000);
        Assert.InRange(score, 0.85, 1.0);
    }

    [Fact]
    public void FastStraightFlingDoesNotTrigger()
    {
        var samples = Enumerable.Range(1, 30)
            .Select(index => new MotionSample(index * 8_000L, 0, 12_000, 0));

        var (trigger, score) = MotionTestData.Run(samples);

        Assert.Null(trigger);
        Assert.InRange(score, 0.45, 0.65);
    }

    [Fact]
    public void MicroJitterDoesNotCountAsReversals()
    {
        var samples = Enumerable.Range(1, 50)
            .Select(index => new MotionSample(
                index * 8_000L,
                0,
                (index % 2 == 0 ? 1 : -1) * 1_500,
                0));

        var detector = new ShakeDetector();
        var results = samples.Select(detector.Process).ToArray();

        Assert.DoesNotContain(results, static result => result.BecameActive);
        Assert.All(results, static result => Assert.Equal(0, result.Metrics.ReversalCount));
    }

    [Fact]
    public void LegacyButtonPolicyResetsAndNeverTriggersWhenExplicitlyEnabled()
    {
        var detector = new ShakeDetector(
            ShakeDetectorOptions.Default with { DisableWhileButtonsPressed = true });
        var results = MotionTestData.Alternating(buttons: PointerButtons.Left)
            .Select(detector.Process)
            .ToArray();

        Assert.All(results, static result =>
        {
            Assert.False(result.IsActive);
            Assert.True(result.WasReset);
            Assert.Equal(0, result.Score);
        });
    }

    [Fact]
    public void ButtonPressAloneDoesNotMasqueradeAsDragByDefault()
    {
        var detector = new ShakeDetector();
        var results = MotionTestData.Alternating(buttons: PointerButtons.Left)
            .Select(detector.Process)
            .ToArray();

        Assert.DoesNotContain(results, static result => result.WasReset);
        Assert.Contains(results, static result => result.BecameActive);
    }

    [Fact]
    public void DiscontinuityReleasesAnActiveDetector()
    {
        var detector = new ShakeDetector();
        var samples = MotionTestData.Alternating();
        foreach (var sample in samples)
        {
            detector.Process(sample);
        }

        var result = detector.Process(new MotionSample(
            samples[^1].TimestampMicroseconds + 8_000,
            0,
            1,
            1,
            Flags: MotionSampleFlags.Discontinuity));

        Assert.True(result.WasReset);
        Assert.True(result.BecameInactive);
        Assert.False(result.IsActive);
    }

    [Fact]
    public void AdvanceTimeExpiresTheWindowAndReleases()
    {
        var detector = new ShakeDetector();
        var samples = MotionTestData.Alternating();
        foreach (var sample in samples)
        {
            detector.Process(sample);
        }

        var results = detector.AdvanceTime(samples[^1].TimestampMicroseconds + 500_000);

        var result = Assert.Single(results);
        Assert.True(result.BecameInactive);
        Assert.False(result.IsActive);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void AdvanceTimeClampsAStaleRenderClockAndPreservesActiveState()
    {
        var detector = new ShakeDetector();
        IReadOnlyList<MotionSample> samples = MotionTestData.Alternating();
        foreach (MotionSample sample in samples)
        {
            _ = detector.Process(sample);
        }

        ShakeActivitySnapshot snapshot = detector.AdvanceTimeSnapshot(
            samples[^1].TimestampMicroseconds - 500);

        Assert.True(snapshot.ClockWasClamped);
        Assert.Equal(samples[^1].TimestampMicroseconds, snapshot.EvaluatedTimestampMicroseconds);
        Assert.True(snapshot.IsActive);
        Assert.Equal(1, snapshot.ActiveDeviceCount);
        Assert.InRange(snapshot.MaximumActiveScore, 0.85, 1.0);
        Assert.True(Assert.Single(snapshot.DeviceResults).IsActive);
    }

    [Fact]
    public void EqualTimestampPacketsAreAccumulatedInsteadOfResettingDevice()
    {
        var detector = new ShakeDetector();

        ShakeDetectionResult first = detector.Process(new MotionSample(10_000, 0, 2_000, 0));
        ShakeDetectionResult second = detector.Process(new MotionSample(10_000, 0, 3_000, 0));
        ShakeDetectionResult third = detector.Process(new MotionSample(11_000, 0, 1_000, 0));

        Assert.False(first.WasReset);
        Assert.False(second.WasReset);
        Assert.False(third.WasReset);
        Assert.Equal(2, third.Metrics.SampleCount);
        Assert.Equal(6_000, third.Metrics.PathMilliDip);
    }

    [Fact]
    public void SeparateDevicesCannotAssembleEachOthersGesture()
    {
        var detector = new ShakeDetector();
        var timestamp = 0L;
        var results = new List<ShakeDetectionResult>();

        for (var stroke = 0; stroke < 4; stroke++)
        {
            var device = stroke % 2;
            var sign = stroke < 2 ? 1 : -1;
            for (var index = 0; index < 10; index++)
            {
                timestamp += 8_000;
                results.Add(detector.Process(new MotionSample(
                    timestamp,
                    device,
                    sign * 12_000,
                    0)));
            }
        }

        Assert.DoesNotContain(results, static result => result.BecameActive);
    }

    [Fact]
    public void TimestampRegressionResetsOnlyAffectedDevice()
    {
        var detector = new ShakeDetector();
        detector.Process(new MotionSample(10_000, 0, 1_000, 0));
        detector.Process(new MotionSample(11_000, 1, 1_000, 0));

        var reset = detector.Process(new MotionSample(9_000, 0, 1_000, 0));
        var other = detector.Process(new MotionSample(12_000, 1, 1_000, 0));

        Assert.True(reset.WasReset);
        Assert.False(other.WasReset);
    }

    [Fact]
    public void CircularMotionIsRejectedByDominantAxisGate()
    {
        var samples = new List<MotionSample>();
        var previousX = 60.0;
        var previousY = 0.0;
        for (var index = 1; index <= 100; index++)
        {
            var angle = index * 2.0 * Math.PI / 25.0;
            var x = 60.0 * Math.Cos(angle);
            var y = 60.0 * Math.Sin(angle);
            samples.Add(new MotionSample(
                index * 4_000L,
                0,
                (int)Math.Round((x - previousX) * 1_000),
                (int)Math.Round((y - previousY) * 1_000)));
            previousX = x;
            previousY = y;
        }

        var (trigger, _) = MotionTestData.Run(samples);

        Assert.Null(trigger);
    }
}
