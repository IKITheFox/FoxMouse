namespace FoxMouse.Core;

public sealed record ShakeDetectorOptions
{
    public static ShakeDetectorOptions Default { get; } = new();

    public long WindowMicroseconds { get; init; } = 420_000;

    public int MinimumPathMilliDip { get; init; } = 240_000;

    public int MinimumStrokeMilliDip { get; init; } = 22_000;

    public int ReversalNoiseMilliDip { get; init; } = 4_000;

    public int MinimumRmsSpeedMilliDipPerSecond { get; init; } = 800_000;

    public int MinimumReversals { get; init; } = 3;

    public long MinimumStrokeMicroseconds { get; init; } = 20_000;

    public long MaximumStrokeMicroseconds { get; init; } = 180_000;

    public double MaximumNetToPathRatio { get; init; } = 0.45;

    public double MinimumDominantAxisRatio { get; init; } = 0.65;

    public double EnterScore { get; init; } = 0.72;

    public double ExitScore { get; init; } = 0.30;

    public int MaximumSampleDeltaMilliDip { get; init; } = 500_000;

    public int MaximumSamplesPerDevice { get; init; } = 8_192;

    /// <summary>
    /// Minimum temporal resolution retained by the detector. Sub-millisecond
    /// packets from high-polling-rate mice are accumulated into one analysis
    /// sample, preserving motion while bounding detector work.
    /// </summary>
    public long AnalysisIntervalMicroseconds { get; init; } = 1_000;

    /// <summary>
    /// Legacy coarse button policy. This is off by default because a pressed
    /// button is not necessarily a drag; callers should use a drag classifier.
    /// </summary>
    public bool DisableWhileButtonsPressed { get; init; }

    public ShakeDetectorOptions Normalize()
    {
        var window = Math.Clamp(WindowMicroseconds, 50_000, 2_000_000);
        var minimumStrokeDuration = Math.Clamp(MinimumStrokeMicroseconds, 1_000, window);
        var maximumStrokeDuration = Math.Clamp(
            MaximumStrokeMicroseconds,
            minimumStrokeDuration,
            window);

        return this with
        {
            WindowMicroseconds = window,
            MinimumPathMilliDip = Math.Clamp(MinimumPathMilliDip, 1_000, 5_000_000),
            MinimumStrokeMilliDip = Math.Clamp(MinimumStrokeMilliDip, 500, 1_000_000),
            ReversalNoiseMilliDip = Math.Clamp(ReversalNoiseMilliDip, 0, 250_000),
            MinimumRmsSpeedMilliDipPerSecond = Math.Clamp(
                MinimumRmsSpeedMilliDipPerSecond,
                1_000,
                20_000_000),
            MinimumReversals = Math.Clamp(MinimumReversals, 1, 20),
            MinimumStrokeMicroseconds = minimumStrokeDuration,
            MaximumStrokeMicroseconds = maximumStrokeDuration,
            MaximumNetToPathRatio = FiniteClamp(MaximumNetToPathRatio, 0.05, 1.0, 0.45),
            MinimumDominantAxisRatio = FiniteClamp(MinimumDominantAxisRatio, 0.5, 1.0, 0.65),
            EnterScore = FiniteClamp(EnterScore, 0.05, 1.0, 0.72),
            ExitScore = FiniteClamp(ExitScore, 0.0, 0.95, 0.30),
            MaximumSampleDeltaMilliDip = Math.Clamp(
                MaximumSampleDeltaMilliDip,
                10_000,
                20_000_000),
            MaximumSamplesPerDevice = Math.Clamp(MaximumSamplesPerDevice, 32, 100_000),
            AnalysisIntervalMicroseconds = Math.Clamp(
                AnalysisIntervalMicroseconds,
                125,
                Math.Min(8_000, window)),
        };
    }

    public static ShakeDetectorOptions ForSensitivity(double sensitivity)
    {
        sensitivity = double.IsFinite(sensitivity) ? Math.Clamp(sensitivity, 0.0, 1.0) : 0.55;
        var thresholdScale = 1.35 - (0.7 * sensitivity);
        var defaults = Default;

        return defaults with
        {
            MinimumPathMilliDip = (int)Math.Round(defaults.MinimumPathMilliDip * thresholdScale),
            MinimumStrokeMilliDip = (int)Math.Round(defaults.MinimumStrokeMilliDip * thresholdScale),
            MinimumRmsSpeedMilliDipPerSecond =
                (int)Math.Round(defaults.MinimumRmsSpeedMilliDipPerSecond * thresholdScale),
        };
    }

    private static double FiniteClamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
