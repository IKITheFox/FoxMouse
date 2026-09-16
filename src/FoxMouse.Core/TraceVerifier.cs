namespace FoxMouse.Core;

public sealed record TraceVerificationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<long> TriggerTimestampsMicroseconds,
    IReadOnlyList<long> ReleaseTimestampsMicroseconds,
    double MaximumScore)
{
    public bool Success => Errors.Count == 0;
}

public static class TraceVerifier
{
    public static TraceVerificationResult Verify(
        MotionTrace trace,
        TraceExpectation expectation,
        ShakeDetectorOptions? detectorOptions = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        TraceExpectationCodec.Validate(expectation);

        var detector = new ShakeDetector(detectorOptions);
        var triggers = new List<long>();
        var releases = new List<long>();
        var errors = new List<string>();
        var maximumScore = 0.0;

        foreach (var sample in trace.Samples)
        {
            var result = detector.Process(sample);
            maximumScore = Math.Max(maximumScore, result.Score);
            CollectTransitions(result, triggers, releases);

            if (expectation.NeverActiveWhileButtonsNonzero
                && sample.Buttons != PointerButtons.None
                && result.IsActive)
            {
                errors.Add($"Detector was active while buttons were pressed at {sample.TimestampMicroseconds} us.");
            }
        }

        foreach (var result in detector.AdvanceTime(trace.EndTimestampMicroseconds))
        {
            maximumScore = Math.Max(maximumScore, result.Score);
            CollectTransitions(result, triggers, releases);
        }

        if (triggers.Count != expectation.TriggerCount)
        {
            errors.Add($"Expected {expectation.TriggerCount} trigger(s), observed {triggers.Count}.");
        }

        if (expectation.FirstTriggerMicroseconds is { } triggerRange)
        {
            if (triggers.Count == 0)
            {
                errors.Add("Expected a first trigger, but the detector never triggered.");
            }
            else if (triggers[0] < triggerRange.Minimum || triggers[0] > triggerRange.Maximum)
            {
                errors.Add(
                    $"First trigger {triggers[0]} us is outside "
                    + $"[{triggerRange.Minimum}, {triggerRange.Maximum}] us.");
            }
        }

        if (expectation.ReleaseByMicroseconds is { } releaseBy
            && expectation.TriggerCount > 0
            && !releases.Any(timestamp => timestamp <= releaseBy))
        {
            errors.Add($"No release was observed by {releaseBy} us.");
        }

        if (expectation.MaximumScore is { } scoreRange
            && (maximumScore < scoreRange.Minimum || maximumScore > scoreRange.Maximum))
        {
            errors.Add(
                $"Maximum score {maximumScore:F6} is outside "
                + $"[{scoreRange.Minimum:F6}, {scoreRange.Maximum:F6}].");
        }

        return new TraceVerificationResult(errors, triggers, releases, maximumScore);
    }

    private static void CollectTransitions(
        ShakeDetectionResult result,
        ICollection<long> triggers,
        ICollection<long> releases)
    {
        if (result.BecameActive)
        {
            triggers.Add(result.TimestampMicroseconds);
        }

        if (result.BecameInactive)
        {
            releases.Add(result.TimestampMicroseconds);
        }
    }
}
