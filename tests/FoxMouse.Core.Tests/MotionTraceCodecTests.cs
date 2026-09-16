using FoxMouse.Core;

namespace FoxMouse.Core.Tests;

public sealed class MotionTraceCodecTests
{
    [Fact]
    public void RoundTripPreservesHeaderSamplesAndEnd()
    {
        var trace = new MotionTrace(
            new MotionTraceHeader { Generator = "test", Seed = 7 },
            MotionTestData.Alternating().Take(3),
            100_000);
        using var writer = new StringWriter();

        MotionTraceCodec.Write(writer, trace);
        using var reader = new StringReader(writer.ToString());
        var roundTrip = MotionTraceCodec.Read(reader);

        Assert.Equal(trace.Header, roundTrip.Header);
        Assert.Equal(trace.Samples, roundTrip.Samples);
        Assert.Equal(trace.EndTimestampMicroseconds, roundTrip.EndTimestampMicroseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"kind\":\"header\",\"schema\":\"foxmouse.motion/1\",\"algorithm\":\"shake-v1\",\"time_unit\":\"us\",\"distance_unit\":\"milli_dip\",\"normalized\":true}")]
    [InlineData("{not-json}")]
    public void MalformedOrTruncatedTraceIsRejected(string json)
    {
        using var reader = new StringReader(json);
        Assert.Throws<MotionTraceFormatException>(() => MotionTraceCodec.Read(reader));
    }

    [Fact]
    public void DuplicateTimestampIsRejected()
    {
        const string json = """
            {"kind":"header","schema":"foxmouse.motion/1","algorithm":"shake-v1","time_unit":"us","distance_unit":"milli_dip","normalized":true}
            {"kind":"sample","t_us":8000,"device":0,"dx_mdip":1,"dy_mdip":0,"buttons":0,"flags":0}
            {"kind":"sample","t_us":8000,"device":0,"dx_mdip":1,"dy_mdip":0,"buttons":0,"flags":0}
            {"kind":"end","t_us":8000,"sample_count":2}
            """;

        using var reader = new StringReader(json);
        Assert.Throws<MotionTraceFormatException>(() => MotionTraceCodec.Read(reader));
    }

    [Fact]
    public void UnknownFieldsAreRejected()
    {
        const string json = """
            {"kind":"header","schema":"foxmouse.motion/1","algorithm":"shake-v1","time_unit":"us","distance_unit":"milli_dip","normalized":true,"surprise":1}
            {"kind":"end","t_us":0,"sample_count":0}
            """;

        using var reader = new StringReader(json);
        Assert.Throws<MotionTraceFormatException>(() => MotionTraceCodec.Read(reader));
    }

    [Fact]
    public void UnknownButtonAndFlagBitsAreRejected()
    {
        const string template = """
            {"kind":"header","schema":"foxmouse.motion/1","algorithm":"shake-v1","time_unit":"us","distance_unit":"milli_dip","normalized":true}
            {"kind":"sample","t_us":8000,"device":0,"dx_mdip":1,"dy_mdip":0,"buttons":BUTTONS,"flags":FLAGS}
            {"kind":"end","t_us":8000,"sample_count":1}
            """;

        using var badButtons = new StringReader(template.Replace("BUTTONS", "64").Replace("FLAGS", "0"));
        using var badFlags = new StringReader(template.Replace("BUTTONS", "0").Replace("FLAGS", "64"));

        Assert.Throws<MotionTraceFormatException>(() => MotionTraceCodec.Read(badButtons));
        Assert.Throws<MotionTraceFormatException>(() => MotionTraceCodec.Read(badFlags));
    }
}
