using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Metrics;

public sealed class TrendAssertionsTests
{
    private static ResourceSample At(long managedBytes) => new(
        SampleIndex: 0, ManagedBytes: managedBytes, ThreadCount: 10, HandleCount: 100);

    [Fact]
    public void NoUpwardDrift_FlatSeries_HasNoDrift()
    {
        var samples = new[] { At(1000), At(1010), At(995), At(1005), At(1000) };

        var result = TrendAssertions.NoUpwardDrift(
            samples, s => s.ManagedBytes, toleranceRatio: 0.10);

        Assert.False(result.HasDrift);
    }

    [Fact]
    public void NoUpwardDrift_MonotonicGrowth_DetectsDrift()
    {
        var samples = new[] { At(1000), At(2000), At(3000), At(4000), At(5000) };

        var result = TrendAssertions.NoUpwardDrift(
            samples, s => s.ManagedBytes, toleranceRatio: 0.10);

        Assert.True(result.HasDrift);
        Assert.Contains("ManagedBytes", result.Detail, StringComparison.Ordinal);
    }
}
