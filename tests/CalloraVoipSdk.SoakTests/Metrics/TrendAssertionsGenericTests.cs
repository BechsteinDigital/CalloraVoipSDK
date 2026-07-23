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

    [Fact]
    public void NoUpwardDrift_SubMsNoise_BelowAbsoluteFloor_HasNoDrift()
    {
        // Reproduziert den CI-Flake: relativ +107 % (0,337 → 0,699 ms), aber beide unter dem 5-ms-Floor.
        var s = new[] { Jitter(0.337), Jitter(0.34), Jitter(0.5), Jitter(0.62), Jitter(0.699) };
        var r = TrendAssertions.NoUpwardDrift(
            s, x => x.JitterMs, toleranceRatio: 0.50, metricName: "JitterMs", absoluteFloor: 5.0);
        Assert.False(r.HasDrift, r.Detail);
        Assert.Contains("Floor=", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoUpwardDrift_RealDriftAboveFloor_StillDetected_WithAbsoluteFloor()
    {
        // Echter Anstieg über den Floor hinaus wird weiterhin gefangen.
        var s = new[] { Jitter(4.0), Jitter(8.0), Jitter(20.0), Jitter(40.0), Jitter(60.0) };
        var r = TrendAssertions.NoUpwardDrift(
            s, x => x.JitterMs, toleranceRatio: 0.50, metricName: "JitterMs", absoluteFloor: 5.0);
        Assert.True(r.HasDrift, r.Detail);
    }
}
