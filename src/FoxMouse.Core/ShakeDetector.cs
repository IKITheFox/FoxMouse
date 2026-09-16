namespace FoxMouse.Core;

/// <summary>
/// Detects rapid alternating pointer motion independently for every input device.
/// The class is deterministic and does not read clocks or operating-system state.
/// </summary>
public sealed class ShakeDetector
{
    private readonly ShakeDetectorOptions _options;
    private readonly Dictionary<int, DeviceState> _devices = [];
    private long _lastObservedTimestamp = -1;

    public ShakeDetector(ShakeDetectorOptions? options = null)
    {
        _options = (options ?? ShakeDetectorOptions.Default).Normalize();
    }

    public ShakeDetectorOptions Options => _options;

    public ShakeActivitySnapshot CurrentActivity => CreateCurrentActivitySnapshot();

    public ShakeDetectionResult Process(MotionSample sample)
    {
        if (sample.TimestampMicroseconds < 0 || sample.DeviceId < 0)
        {
            return ShakeDetectionResult.Empty(sample.TimestampMicroseconds, sample.DeviceId, wasReset: true);
        }

        _lastObservedTimestamp = Math.Max(_lastObservedTimestamp, sample.TimestampMicroseconds);

        if (!_devices.TryGetValue(sample.DeviceId, out var state))
        {
            state = new DeviceState();
            _devices.Add(sample.DeviceId, state);
        }

        if (sample.TimestampMicroseconds < state.LastTimestampMicroseconds)
        {
            state.Reset();
            state.LastTimestampMicroseconds = sample.TimestampMicroseconds;
            ShakeDetectionResult reset = ShakeDetectionResult.Empty(
                sample.TimestampMicroseconds,
                sample.DeviceId,
                wasReset: true);
            state.LastResult = reset;
            return reset;
        }

        state.LastTimestampMicroseconds = sample.TimestampMicroseconds;

        if (ShouldReset(sample))
        {
            var wasActive = state.IsActive;
            state.Reset(keepTimestamp: true);
            ShakeDetectionResult reset = new(
                sample.TimestampMicroseconds,
                sample.DeviceId,
                0,
                false,
                false,
                wasActive,
                true,
                ShakeMetrics.Empty);
            state.LastResult = reset;
            return reset;
        }

        AddAnalysisSample(state, sample);
        Prune(state, sample.TimestampMicroseconds);

        if (state.Samples.Count > _options.MaximumSamplesPerDevice)
        {
            var removeCount = state.Samples.Count - _options.MaximumSamplesPerDevice;
            state.Samples.RemoveRange(0, removeCount);
        }

        return Evaluate(sample.DeviceId, state, sample.TimestampMicroseconds, wasReset: false);
    }

    public IReadOnlyList<ShakeDetectionResult> AdvanceTime(long timestampMicroseconds)
        => AdvanceTimeSnapshot(timestampMicroseconds).DeviceResults;

    public ShakeActivitySnapshot AdvanceTimeSnapshot(long timestampMicroseconds)
    {
        if (timestampMicroseconds < 0)
        {
            return CreateCurrentActivitySnapshot(
                requestedTimestampMicroseconds: timestampMicroseconds,
                clockWasClamped: false);
        }

        // Raw Input timestamps and the render clock share a monotonic origin,
        // but reconstructing several sub-millisecond packets can put the newest
        // packet fractionally ahead of a render tick. Evaluating at the newest
        // known time preserves active state instead of returning an ambiguous
        // empty list that callers can mistake for inactivity.
        bool clockWasClamped = timestampMicroseconds < _lastObservedTimestamp;
        long evaluationTimestamp = Math.Max(timestampMicroseconds, _lastObservedTimestamp);
        _lastObservedTimestamp = evaluationTimestamp;
        var results = new List<ShakeDetectionResult>(_devices.Count);
        int activeDeviceCount = 0;
        double maximumActiveScore = 0;

        foreach (var pair in _devices.OrderBy(static pair => pair.Key))
        {
            var state = pair.Value;
            Prune(state, evaluationTimestamp);
            ShakeDetectionResult result = Evaluate(
                pair.Key,
                state,
                evaluationTimestamp,
                wasReset: false);
            results.Add(result);
            if (result.IsActive)
            {
                activeDeviceCount++;
                maximumActiveScore = Math.Max(maximumActiveScore, result.Score);
            }
        }

        return new ShakeActivitySnapshot(
            timestampMicroseconds,
            evaluationTimestamp,
            clockWasClamped,
            _devices.Count,
            activeDeviceCount,
            maximumActiveScore,
            results);
    }

    public bool ResetDevice(int deviceId)
    {
        if (!_devices.Remove(deviceId, out var state))
        {
            return false;
        }

        state.Reset();
        return true;
    }

    public void ResetAll()
    {
        _devices.Clear();
        _lastObservedTimestamp = -1;
    }

    private bool ShouldReset(MotionSample sample)
    {
        if ((sample.Flags & (MotionSampleFlags.Discontinuity | MotionSampleFlags.DeviceChanged)) != 0)
        {
            return true;
        }

        if (_options.DisableWhileButtonsPressed && sample.Buttons != PointerButtons.None)
        {
            return true;
        }

        return Hypot(sample.DeltaXMilliDip, sample.DeltaYMilliDip)
            > _options.MaximumSampleDeltaMilliDip;
    }

    private void AddAnalysisSample(DeviceState state, MotionSample sample)
    {
        if (state.Samples.Count > 0 &&
            sample.TimestampMicroseconds - state.AnalysisBucketStartMicroseconds
                < _options.AnalysisIntervalMicroseconds)
        {
            MotionSample previous = state.Samples[^1];
            state.Samples[^1] = sample with
            {
                DeltaXMilliDip = SaturatingAdd(previous.DeltaXMilliDip, sample.DeltaXMilliDip),
                DeltaYMilliDip = SaturatingAdd(previous.DeltaYMilliDip, sample.DeltaYMilliDip),
            };
            return;
        }

        state.Samples.Add(sample);
        state.AnalysisBucketStartMicroseconds = sample.TimestampMicroseconds;
    }

    private void Prune(DeviceState state, long nowMicroseconds)
    {
        var cutoff = nowMicroseconds - _options.WindowMicroseconds;
        var removeCount = 0;

        while (removeCount < state.Samples.Count
            && state.Samples[removeCount].TimestampMicroseconds < cutoff)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            state.Samples.RemoveRange(0, removeCount);
        }
    }

    private ShakeDetectionResult Evaluate(
        int deviceId,
        DeviceState state,
        long timestampMicroseconds,
        bool wasReset)
    {
        var metrics = ComputeMetrics(state);
        var score = ComputeScore(metrics);
        var eligible = IsEligible(metrics);
        var wasActive = state.IsActive;

        if (!state.IsActive)
        {
            state.IsActive = eligible && score >= _options.EnterScore;
        }
        else if (!eligible || score <= _options.ExitScore)
        {
            state.IsActive = false;
        }

        ShakeDetectionResult result = new(
            timestampMicroseconds,
            deviceId,
            score,
            state.IsActive,
            !wasActive && state.IsActive,
            wasActive && !state.IsActive,
            wasReset,
            metrics);
        state.LastResult = result;
        return result;
    }

    private ShakeMetrics ComputeMetrics(DeviceState state)
    {
        List<MotionSample> samples = state.Samples;
        if (samples.Count < 2)
        {
            return ShakeMetrics.Empty with { SampleCount = samples.Count };
        }

        double path = 0;
        double netX = 0;
        double netY = 0;
        double covarianceXX = 0;
        double covarianceXY = 0;
        double covarianceYY = 0;
        double weightedVelocitySquared = 0;
        long velocityDuration = 0;

        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var dx = (double)sample.DeltaXMilliDip;
            var dy = (double)sample.DeltaYMilliDip;
            var length = Hypot(dx, dy);

            path += length;
            netX += dx;
            netY += dy;
            covarianceXX += dx * dx;
            covarianceXY += dx * dy;
            covarianceYY += dy * dy;

            if (index == 0)
            {
                continue;
            }

            var deltaTime = sample.TimestampMicroseconds - samples[index - 1].TimestampMicroseconds;
            if (deltaTime <= 0)
            {
                continue;
            }

            var velocity = length * MotionSample.MicrosecondsPerSecond / deltaTime;
            weightedVelocitySquared += velocity * velocity * deltaTime;
            velocityDuration += deltaTime;
        }

        var trace = covarianceXX + covarianceYY;
        var discriminant = Hypot(covarianceXX - covarianceYY, 2.0 * covarianceXY);
        var largestEigenvalue = (trace + discriminant) / 2.0;
        var dominantAxisRatio = trace <= double.Epsilon
            ? 0.5
            : Math.Clamp(largestEigenvalue / trace, 0.5, 1.0);

        var angle = 0.5 * Math.Atan2(2.0 * covarianceXY, covarianceXX - covarianceYY);
        var axisX = Math.Cos(angle);
        var axisY = Math.Sin(angle);
        var reversals = CountReversals(state, axisX, axisY);
        var rmsSpeed = velocityDuration <= 0
            ? 0
            : Math.Sqrt(weightedVelocitySquared / velocityDuration);

        return new ShakeMetrics(
            path,
            Hypot(netX, netY),
            rmsSpeed,
            dominantAxisRatio,
            reversals,
            samples.Count);
    }

    private int CountReversals(DeviceState state, double axisX, double axisY)
    {
        List<MotionSample> samples = state.Samples;
        List<StrokeRun> rawRuns = state.RawRuns;
        List<StrokeRun> denoised = state.DenoisedRuns;
        rawRuns.Clear();
        denoised.Clear();

        foreach (MotionSample sample in samples)
        {
            var projection = (sample.DeltaXMilliDip * axisX) + (sample.DeltaYMilliDip * axisY);
            if (Math.Abs(projection) < 1.0)
            {
                continue;
            }

            var sign = projection > 0 ? 1 : -1;
            if (rawRuns.Count == 0 || rawRuns[^1].Sign != sign)
            {
                rawRuns.Add(new StrokeRun(sign, Math.Abs(projection), sample.TimestampMicroseconds, sample.TimestampMicroseconds));
            }
            else
            {
                var current = rawRuns[^1];
                rawRuns[^1] = current with
                {
                    DistanceMilliDip = current.DistanceMilliDip + Math.Abs(projection),
                    EndMicroseconds = sample.TimestampMicroseconds,
                };
            }
        }

        foreach (var run in rawRuns)
        {
            if (run.DistanceMilliDip < _options.ReversalNoiseMilliDip)
            {
                continue;
            }

            if (denoised.Count > 0 && denoised[^1].Sign == run.Sign)
            {
                var previous = denoised[^1];
                denoised[^1] = previous with
                {
                    DistanceMilliDip = previous.DistanceMilliDip + run.DistanceMilliDip,
                    EndMicroseconds = run.EndMicroseconds,
                };
            }
            else
            {
                denoised.Add(run);
            }
        }

        var validRuns = 0;
        foreach (var run in denoised)
        {
            var duration = run.EndMicroseconds - run.StartMicroseconds;
            if (run.DistanceMilliDip >= _options.MinimumStrokeMilliDip
                && duration >= _options.MinimumStrokeMicroseconds
                && duration <= _options.MaximumStrokeMicroseconds)
            {
                validRuns++;
            }
        }

        return Math.Max(0, validRuns - 1);
    }

    private double ComputeScore(ShakeMetrics metrics)
    {
        var speed = Ratio(metrics.RmsSpeedMilliDipPerSecond, _options.MinimumRmsSpeedMilliDipPerSecond);
        var reversals = Ratio(metrics.ReversalCount, _options.MinimumReversals);
        var path = Ratio(metrics.PathMilliDip, _options.MinimumPathMilliDip);
        var confinement = Math.Clamp(
            (_options.MaximumNetToPathRatio - metrics.NetToPathRatio)
            / _options.MaximumNetToPathRatio,
            0.0,
            1.0);
        var linearity = Math.Clamp(
            (metrics.DominantAxisRatio - _options.MinimumDominantAxisRatio)
            / (1.0 - _options.MinimumDominantAxisRatio),
            0.0,
            1.0);

        var score = (0.28 * speed)
            + (0.25 * reversals)
            + (0.20 * path)
            + (0.15 * confinement)
            + (0.12 * linearity);

        return double.IsFinite(score) ? Math.Clamp(score, 0.0, 1.0) : 0.0;
    }

    private bool IsEligible(ShakeMetrics metrics) =>
        metrics.PathMilliDip >= _options.MinimumPathMilliDip
        && metrics.RmsSpeedMilliDipPerSecond >= _options.MinimumRmsSpeedMilliDipPerSecond
        && metrics.ReversalCount >= _options.MinimumReversals
        && metrics.NetToPathRatio <= _options.MaximumNetToPathRatio
        && metrics.DominantAxisRatio >= _options.MinimumDominantAxisRatio;

    private static double Ratio(double value, double threshold) =>
        threshold <= 0 ? 1.0 : Math.Clamp(value / threshold, 0.0, 1.0);

    private ShakeActivitySnapshot CreateCurrentActivitySnapshot(
        long? requestedTimestampMicroseconds = null,
        bool clockWasClamped = false)
    {
        var results = new List<ShakeDetectionResult>(_devices.Count);
        int activeDeviceCount = 0;
        double maximumActiveScore = 0;

        foreach (var pair in _devices.OrderBy(static pair => pair.Key))
        {
            ShakeDetectionResult result = pair.Value.LastResult;
            results.Add(result);
            if (result.IsActive)
            {
                activeDeviceCount++;
                maximumActiveScore = Math.Max(maximumActiveScore, result.Score);
            }
        }

        long evaluatedTimestamp = Math.Max(0, _lastObservedTimestamp);
        return new ShakeActivitySnapshot(
            requestedTimestampMicroseconds ?? evaluatedTimestamp,
            evaluatedTimestamp,
            clockWasClamped,
            _devices.Count,
            activeDeviceCount,
            maximumActiveScore,
            results);
    }

    private static int SaturatingAdd(int left, int right) =>
        (int)Math.Clamp((long)left + right, int.MinValue, int.MaxValue);

    private static double Hypot(double x, double y)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        var maximum = Math.Max(x, y);
        if (maximum == 0)
        {
            return 0;
        }

        var normalizedX = x / maximum;
        var normalizedY = y / maximum;
        return maximum * Math.Sqrt((normalizedX * normalizedX) + (normalizedY * normalizedY));
    }

    private sealed class DeviceState
    {
        public List<MotionSample> Samples { get; } = [];

        public List<StrokeRun> RawRuns { get; } = [];

        public List<StrokeRun> DenoisedRuns { get; } = [];

        public long LastTimestampMicroseconds { get; set; } = -1;

        public long AnalysisBucketStartMicroseconds { get; set; } = -1;

        public bool IsActive { get; set; }

        public ShakeDetectionResult LastResult { get; set; }

        public void Reset(bool keepTimestamp = false)
        {
            Samples.Clear();
            RawRuns.Clear();
            DenoisedRuns.Clear();
            AnalysisBucketStartMicroseconds = -1;
            IsActive = false;
            LastResult = default;
            if (!keepTimestamp)
            {
                LastTimestampMicroseconds = -1;
            }
        }
    }

    private readonly record struct StrokeRun(
        int Sign,
        double DistanceMilliDip,
        long StartMicroseconds,
        long EndMicroseconds);
}
