using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class ScaleAnimatorTests
{
    [Fact]
    public void HighScoreSmoothlyApproachesMaximumWithoutOvershoot()
    {
        var animator = new ScaleAnimator();
        animator.Step(0, 1);
        var scales = new List<double>();
        for (var timestamp = 8_000L; timestamp <= 600_000; timestamp += 8_000)
        {
            scales.Add(animator.Step(timestamp, 1).CurrentScale);
        }

        Assert.True(scales.Zip(scales.Skip(1)).All(static pair => pair.First <= pair.Second));
        Assert.All(scales, static scale => Assert.InRange(scale, 1.0, 3.5));
        Assert.InRange(scales[^1], 3.49, 3.5);
    }

    [Fact]
    public void LowScoreHoldsThenReturnsToOne()
    {
        var options = new ScaleAnimationOptions
        {
            HoldMicroseconds = 100_000,
            ReleaseTimeConstantMicroseconds = 50_000,
        };
        var animator = new ScaleAnimator(options);
        animator.Step(0, 1);
        var grown = animator.Step(100_000, 1);
        var held = animator.Step(199_999, 0);
        var releasing = animator.Step(200_001, 0);

        Assert.True(grown.CurrentScale > 1);
        Assert.True(held.IsHolding);
        Assert.True(held.TargetScale > 1);
        Assert.False(releasing.IsHolding);
        Assert.Equal(1, releasing.TargetScale);

        var state = releasing;
        for (var timestamp = 210_000L; timestamp <= 1_000_000; timestamp += 10_000)
        {
            state = ScaleAnimator.Advance(state, timestamp, 0, options);
        }

        Assert.True(state.IsAtRest);
    }

    [Fact]
    public void AttackIsFasterThanReleaseForEqualElapsedTime()
    {
        var options = new ScaleAnimationOptions
        {
            HoldMicroseconds = 0,
            AttackTimeConstantMicroseconds = 50_000,
            ReleaseTimeConstantMicroseconds = 300_000,
            MaximumStepMicroseconds = 500_000,
        };
        var rising = ScaleAnimationState.Initial;
        rising = ScaleAnimator.Advance(rising, 0, 1, options);
        rising = ScaleAnimator.Advance(rising, 100_000, 1, options);
        var upwardFraction = (rising.CurrentScale - 1) / (options.MaximumScale - 1);

        var fallingStart = new ScaleAnimationState(3.5, 3.5, 3.5, 0, 0, false);
        var falling = ScaleAnimator.Advance(fallingStart, 100_000, 0, options);
        var downwardFraction = (3.5 - falling.CurrentScale) / (3.5 - 1);

        Assert.True(upwardFraction > downwardFraction);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-10)]
    public void InvalidOrNegativeScoreBehavesAsNoSignal(double score)
    {
        var state = ScaleAnimator.Advance(ScaleAnimationState.Initial, 0, score);

        Assert.Equal(1, state.TargetScale);
        Assert.Equal(1, state.CurrentScale);
    }

    [Fact]
    public void TimeRegressionSafelyResetsScale()
    {
        var state = ScaleAnimator.Advance(ScaleAnimationState.Initial, 100, 1);
        state = ScaleAnimator.Advance(state, 200, 1);

        var reset = ScaleAnimator.Advance(state, 50, 1);

        Assert.Equal(1, reset.CurrentScale);
        Assert.Equal(50, reset.LastTimestampMicroseconds);
    }

    [Fact]
    public void TargetScaleIsContinuousAndMonotonicWithScore()
    {
        var targets = Enumerable.Range(30, 71)
            .Select(score => ScaleAnimator.Advance(
                ScaleAnimationState.Initial,
                0,
                score / 100.0).TargetScale)
            .ToArray();

        Assert.True(targets.Zip(targets.Skip(1)).All(static pair => pair.First <= pair.Second));
        Assert.Equal(1, targets[0]);
        Assert.Equal(3.5, targets[^1], 10);
    }

    [Fact]
    public void ReleaseUsesAControlledElasticUndershootAndSettles()
    {
        var options = new ScaleAnimationOptions
        {
            HoldMicroseconds = 0,
            ReleaseTimeConstantMicroseconds = 280_000,
            ReleaseDampingRatio = 0.72,
            MinimumScale = 0.975,
        };
        var animator = new ScaleAnimator(options);
        animator.Step(0, 1);
        animator.Step(160_000, 1);

        var releaseScales = new List<double>();
        ScaleAnimationState state = animator.State;
        for (long timestamp = 168_000; timestamp <= 2_000_000; timestamp += 8_000)
        {
            state = animator.Step(timestamp, 0);
            releaseScales.Add(state.CurrentScale);
        }

        Assert.InRange(releaseScales.Min(), 0.975, 0.9999);
        Assert.True(state.IsAtRest);
        Assert.Equal(1, state.CurrentScale);
        Assert.Equal(0, state.Velocity);
    }

    [Fact]
    public void AnalyticSpringIsFrameRateIndependentForConstantDrive()
    {
        var eightMillisecond = new ScaleAnimator();
        var sixteenMillisecond = new ScaleAnimator();
        eightMillisecond.Step(0, 1);
        sixteenMillisecond.Step(0, 1);

        for (long timestamp = 8_000; timestamp <= 192_000; timestamp += 8_000)
        {
            eightMillisecond.Step(timestamp, 1);
        }

        for (long timestamp = 16_000; timestamp <= 192_000; timestamp += 16_000)
        {
            sixteenMillisecond.Step(timestamp, 1);
        }

        Assert.Equal(
            eightMillisecond.State.CurrentScale,
            sixteenMillisecond.State.CurrentScale,
            precision: 10);
        Assert.Equal(
            eightMillisecond.State.Velocity,
            sixteenMillisecond.State.Velocity,
            precision: 10);
    }

    [Fact]
    public void RestRequiresTwoStableFrames()
    {
        var oneFrame = new ScaleAnimationState(1, 1, 1, 100, -1, false, 0, 1);
        var twoFrames = oneFrame with { RestFrameCount = 2 };

        Assert.False(oneFrame.IsAtRest);
        Assert.True(twoFrames.IsAtRest);
    }
}
