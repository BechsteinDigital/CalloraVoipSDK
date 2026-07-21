using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Metrics;

public sealed class TrendAssertionsGenericTests
{
    private static MediaQualitySnapshot Jitter(double ms) => new(
        CapturedAtUtc: default, JitterMs: ms, RoundTripTimeMs: 0,
        PacketsDelivered: 0, PacketsDroppedLate: 0, PacketsDroppedOverflow: 0, PacketsUnrecoverableLoss: 0);

    [Fact]
    public void NoUpwardDrift_FlatJitter_HasNoDrift()
    {
        var s = new[] { Jitter(5.0), Jitter(5.2), Jitter(4.8), Jitter(5.1), Jitter(5.0) };
        var r = TrendAssertions.NoUpwardDrift(s, x => x.JitterMs, toleranceRatio: 0.20, metricName: "JitterMs");
        Assert.False(r.HasDrift);
    }

    [Fact]
    public void NoUpwardDrift_RisingJitter_DetectsDrift()
    {
        var s = new[] { Jitter(5.0), Jitter(10.0), Jitter(20.0), Jitter(35.0), Jitter(60.0) };
        var r = TrendAssertions.NoUpwardDrift(s, x => x.JitterMs, toleranceRatio: 0.20, metricName: "JitterMs");
        Assert.True(r.HasDrift);
        Assert.Contains("JitterMs", r.Detail, StringComparison.Ordinal);
    }
}
