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

    [Fact]
    public void NoUpwardDrift_ZeroStartBaseline_MinimalGrowth_HasNoDrift()
    {
        // Startsockel 0 (z. B. HandleCount auf Linux) + minimales Wachstum darf keinen Fehlalarm geben.
        var samples = new[]
        {
            new ResourceSample(0, ManagedBytes: 0, ThreadCount: 10, HandleCount: 0),
            new ResourceSample(1, ManagedBytes: 0, ThreadCount: 10, HandleCount: 0),
            new ResourceSample(2, ManagedBytes: 1, ThreadCount: 10, HandleCount: 0),
        };

        var result = TrendAssertions.NoUpwardDrift(samples, s => s.ManagedBytes, toleranceRatio: 0.10);

        Assert.False(result.HasDrift);
    }
}
