namespace FoxMouse.Core;

public sealed record ScaleAnimationOptions
{
    public static ScaleAnimationOptions Default { get; } = new();

    public double MaximumScale { get; init; } = 3.5;

    public double ActiveScoreThreshold { get; init; } = 0.30;

    public long AttackTimeConstantMicroseconds { get; init; } = 75_000;

    public long ReleaseTimeConstantMicroseconds { get; init; } = 280_000;

    public long HoldMicroseconds { get; init; } = 180_000;

    public long MaximumStepMicroseconds { get; init; } = 100_000;

    /// <summary>
    /// Damping used while the cursor grows. Critical damping keeps the
    /// pointer responsive without overshooting the configured maximum.
    /// </summary>
    public double AttackDampingRatio { get; init; } = 1.0;

    /// <summary>
    /// Slight under-damping gives the return animation a small, controlled
    /// amount of elasticity instead of an exponential-looking fade.
    /// </summary>
    public double ReleaseDampingRatio { get; init; } = 0.78;

    public double MinimumScale { get; init; } = 0.975;

    public double RestEpsilon { get; init; } = 0.0025;

    public double RestVelocityEpsilon { get; init; } = 0.02;

    public int RequiredRestFrames { get; init; } = 2;

    public ScaleAnimationOptions Normalize() => this with
    {
        MaximumScale = FiniteClamp(MaximumScale, 1.0, 8.0, 3.5),
        ActiveScoreThreshold = FiniteClamp(ActiveScoreThreshold, 0.0, 0.95, 0.30),
        AttackTimeConstantMicroseconds = Math.Clamp(AttackTimeConstantMicroseconds, 1_000, 2_000_000),
        ReleaseTimeConstantMicroseconds = Math.Clamp(ReleaseTimeConstantMicroseconds, 1_000, 5_000_000),
        HoldMicroseconds = Math.Clamp(HoldMicroseconds, 0, 2_000_000),
        MaximumStepMicroseconds = Math.Clamp(MaximumStepMicroseconds, 1_000, 1_000_000),
        AttackDampingRatio = FiniteClamp(AttackDampingRatio, 0.5, 2.0, 1.0),
        ReleaseDampingRatio = FiniteClamp(ReleaseDampingRatio, 0.5, 2.0, 0.78),
        MinimumScale = FiniteClamp(MinimumScale, 0.90, 1.0, 0.975),
        RestEpsilon = FiniteClamp(RestEpsilon, 0.000_001, 0.1, 0.0025),
        RestVelocityEpsilon = FiniteClamp(RestVelocityEpsilon, 0.000_001, 10.0, 0.02),
        RequiredRestFrames = Math.Clamp(RequiredRestFrames, 1, 8),
    };

    private static double FiniteClamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public readonly record struct ScaleAnimationState(
    double CurrentScale,
    double TargetScale,
    double HeldScale,
    long LastTimestampMicroseconds,
    long LastDrivenTimestampMicroseconds,
    bool IsHolding,
    double Velocity = 0,
    int RestFrameCount = 0)
{
    public static ScaleAnimationState Initial { get; } = new(1, 1, 1, -1, -1, false, 0, 0);

    public bool IsAtRest => CurrentScale == 1.0 &&
                            TargetScale == 1.0 &&
                            Velocity == 0 &&
                            !IsHolding &&
                            RestFrameCount >= 2;
}

/// <summary>
/// Converts detector score into a smooth cursor scale. Position is intentionally
/// not animated; only visual scale is filtered.
/// </summary>
public sealed class ScaleAnimator
{
    private readonly ScaleAnimationOptions _options;

    public ScaleAnimator(ScaleAnimationOptions? options = null)
    {
        _options = (options ?? ScaleAnimationOptions.Default).Normalize();
    }

    public ScaleAnimationState State { get; private set; } = ScaleAnimationState.Initial;

    public ScaleAnimationState Step(long timestampMicroseconds, double shakeScore)
    {
        State = Advance(State, timestampMicroseconds, shakeScore, _options);
        return State;
    }

    public void Reset() => State = ScaleAnimationState.Initial;

    public static ScaleAnimationState Advance(
        ScaleAnimationState state,
        long timestampMicroseconds,
        double shakeScore,
        ScaleAnimationOptions? options = null)
    {
        if (timestampMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampMicroseconds));
        }

        var normalizedOptions = (options ?? ScaleAnimationOptions.Default).Normalize();
        shakeScore = double.IsFinite(shakeScore) ? Math.Clamp(shakeScore, 0.0, 1.0) : 0.0;

        if (state.LastTimestampMicroseconds < 0)
        {
            var initialTarget = TargetForScore(shakeScore, normalizedOptions);
            var driven = shakeScore >= normalizedOptions.ActiveScoreThreshold
                ? timestampMicroseconds
                : -1;
            return new ScaleAnimationState(
                1.0,
                initialTarget,
                initialTarget,
                timestampMicroseconds,
                driven,
                driven >= 0,
                0,
                0);
        }

        if (timestampMicroseconds < state.LastTimestampMicroseconds)
        {
            return ScaleAnimationState.Initial with { LastTimestampMicroseconds = timestampMicroseconds };
        }

        var signalActive = shakeScore >= normalizedOptions.ActiveScoreThreshold;
        var target = state.TargetScale;
        var heldScale = state.HeldScale;
        var lastDriven = state.LastDrivenTimestampMicroseconds;
        var holding = false;

        if (signalActive)
        {
            target = TargetForScore(shakeScore, normalizedOptions);
            heldScale = target;
            lastDriven = timestampMicroseconds;
            holding = true;
        }
        else if (lastDriven >= 0
            && timestampMicroseconds - lastDriven <= normalizedOptions.HoldMicroseconds)
        {
            target = heldScale;
            holding = true;
        }
        else
        {
            target = 1.0;
        }

        if (timestampMicroseconds == state.LastTimestampMicroseconds)
        {
            return state with
            {
                TargetScale = target,
                HeldScale = heldScale,
                LastDrivenTimestampMicroseconds = lastDriven,
                IsHolding = holding,
                RestFrameCount = 0,
            };
        }

        long elapsedMicroseconds = Math.Min(
            timestampMicroseconds - state.LastTimestampMicroseconds,
            normalizedOptions.MaximumStepMicroseconds);
        double timeConstantMicroseconds = target > state.CurrentScale
            ? normalizedOptions.AttackTimeConstantMicroseconds
            : normalizedOptions.ReleaseTimeConstantMicroseconds;
        double dampingRatio = target > state.CurrentScale
            ? normalizedOptions.AttackDampingRatio
            : normalizedOptions.ReleaseDampingRatio;

        // Four time constants is approximately the 2% settling time of a
        // critically damped system. This preserves the existing public timing
        // knobs while replacing the first-order exponential with a physical,
        // frame-rate-independent spring.
        double angularFrequency = 4_000_000d / timeConstantMicroseconds;
        double elapsedSeconds = elapsedMicroseconds / 1_000_000d;
        (double current, double velocity) = IntegrateSpring(
            state.CurrentScale,
            state.Velocity,
            target,
            angularFrequency,
            dampingRatio,
            elapsedSeconds);

        if (current > normalizedOptions.MaximumScale)
        {
            current = normalizedOptions.MaximumScale;
            velocity = Math.Min(0, velocity);
        }
        else if (current < normalizedOptions.MinimumScale)
        {
            current = normalizedOptions.MinimumScale;
            velocity = Math.Max(0, velocity);
        }

        int restFrames = 0;
        if (!holding &&
            target == 1.0 &&
            Math.Abs(current - 1.0) <= normalizedOptions.RestEpsilon &&
            Math.Abs(velocity) <= normalizedOptions.RestVelocityEpsilon)
        {
            restFrames = state.RestFrameCount + 1;
            if (restFrames >= normalizedOptions.RequiredRestFrames)
            {
                current = 1.0;
                velocity = 0;
                // IsAtRest deliberately uses two stable frames as its public
                // contract; keep that invariant even if an option requests a
                // larger debounce count.
                restFrames = Math.Max(2, restFrames);
            }
        }

        return new ScaleAnimationState(
            current,
            target,
            heldScale,
            timestampMicroseconds,
            lastDriven,
            holding,
            velocity,
            restFrames);
    }

    private static (double Position, double Velocity) IntegrateSpring(
        double position,
        double velocity,
        double target,
        double angularFrequency,
        double dampingRatio,
        double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
        {
            return (position, velocity);
        }

        double displacement = position - target;
        if (dampingRatio < 1.0 - 1e-6)
        {
            double dampedFrequency = angularFrequency * Math.Sqrt(1.0 - (dampingRatio * dampingRatio));
            double decay = Math.Exp(-dampingRatio * angularFrequency * elapsedSeconds);
            double cosine = Math.Cos(dampedFrequency * elapsedSeconds);
            double sine = Math.Sin(dampedFrequency * elapsedSeconds);
            double sineCoefficient =
                (velocity + (dampingRatio * angularFrequency * displacement)) / dampedFrequency;
            double nextDisplacement = decay * ((displacement * cosine) + (sineCoefficient * sine));
            double nextVelocity = decay *
                ((velocity * cosine) -
                 (((dampingRatio * angularFrequency * velocity) +
                   (angularFrequency * angularFrequency * displacement)) /
                  dampedFrequency * sine));
            return (target + nextDisplacement, nextVelocity);
        }

        if (dampingRatio <= 1.0 + 1e-6)
        {
            double decay = Math.Exp(-angularFrequency * elapsedSeconds);
            double coefficient = velocity + (angularFrequency * displacement);
            double nextDisplacement = decay * (displacement + (coefficient * elapsedSeconds));
            double nextVelocity = decay *
                (velocity - (angularFrequency * coefficient * elapsedSeconds));
            return (target + nextDisplacement, nextVelocity);
        }

        double root = Math.Sqrt((dampingRatio * dampingRatio) - 1.0);
        double firstRate = -angularFrequency * (dampingRatio - root);
        double secondRate = -angularFrequency * (dampingRatio + root);
        double firstCoefficient = (velocity - (secondRate * displacement)) / (firstRate - secondRate);
        double secondCoefficient = displacement - firstCoefficient;
        double firstTerm = firstCoefficient * Math.Exp(firstRate * elapsedSeconds);
        double secondTerm = secondCoefficient * Math.Exp(secondRate * elapsedSeconds);
        return (
            target + firstTerm + secondTerm,
            (firstRate * firstTerm) + (secondRate * secondTerm));
    }

    private static double TargetForScore(double score, ScaleAnimationOptions options)
    {
        if (score < options.ActiveScoreThreshold)
        {
            return 1.0;
        }

        var normalized = (score - options.ActiveScoreThreshold) / (1.0 - options.ActiveScoreThreshold);
        var smooth = normalized * normalized * (3.0 - (2.0 * normalized));
        return 1.0 + ((options.MaximumScale - 1.0) * smooth);
    }
}
