using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class TraceVerifierTests
{
    [Fact]
    public void PositiveCorpusTracePassesExpectation()
    {
        var trace = new MotionTrace(
            new MotionTraceHeader { Generator = "test" },
            MotionTestData.Alternating(),
            900_000);
        var expectation = new TraceExpectation
        {
            TriggerCount = 1,
            FirstTriggerMicroseconds = new LongRangeExpectation
            {
                Minimum = 240_000,
                Maximum = 360_000,
            },
            ReleaseByMicroseconds = 900_000,
            MaximumScore = new DoubleRangeExpectation { Minimum = 0.85, Maximum = 1.0 },
        };

        var result = TraceVerifier.Verify(trace, expectation);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Single(result.TriggerTimestampsMicroseconds);
        Assert.Single(result.ReleaseTimestampsMicroseconds);
    }

    [Fact]
    public void IncorrectExpectationProducesActionableErrors()
    {
        var trace = new MotionTrace(
            new MotionTraceHeader(),
            MotionTestData.Alternating(),
            900_000);
        var expectation = new TraceExpectation
        {
            TriggerCount = 0,
            MaximumScore = new DoubleRangeExpectation { Minimum = 0, Maximum = 0.1 },
        };

        var result = TraceVerifier.Verify(trace, expectation);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, static error => error.Contains("trigger", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, static error => error.Contains("score", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CheckedInCorpusPasses()
    {
        var corpus = Path.Combine(AppContext.BaseDirectory, "data");
        var traces = Directory.GetFiles(corpus, "*.fmotion.jsonl");
        Assert.NotEmpty(traces);

        foreach (var tracePath in traces)
        {
            var expectationPath = tracePath.Replace(
                ".fmotion.jsonl",
                ".expect.json",
                StringComparison.OrdinalIgnoreCase);
            var result = TraceVerifier.Verify(
                MotionTraceCodec.Read(tracePath),
                TraceExpectationCodec.Read(expectationPath));

            Assert.True(result.Success, $"{Path.GetFileName(tracePath)}: {string.Join("; ", result.Errors)}");
        }
    }
}
