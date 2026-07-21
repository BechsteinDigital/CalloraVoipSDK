using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Metrics;

public sealed class NoUpwardSlopeTests
{
    private static ResourceSample Managed(int index, long managedBytes) => new(
        SampleIndex: index, ManagedBytes: managedBytes, PrivateMemoryBytes: 0, WorkingSetBytes: 0,
        ThreadCount: 10, HandleCount: 0, FileDescriptorCount: 0, SocketDescriptorCount: 0);

    [Fact]
    public void LeastSquaresSlope_MonotonicLine_MatchesExpectedSlope()
    {
        // y = 1000 + 50·x ⇒ Steigung exakt 50.
        var ys = new double[] { 1000, 1050, 1100, 1150, 1200 };
        var slope = TrendAssertions.LeastSquaresSlope(ys);
        Assert.Equal(50d, slope, precision: 6);
    }

    [Fact]
    public void NoUpwardSlope_OscillatingFlatSeries_NoDrift()
    {
        // Um ~1_850_000 oszillierend (echte warme Heap-Signatur), kein Aufwärtstrend.
        var samples = new[]
        {
            Managed(0, 1_850_000), Managed(1, 1_862_000), Managed(2, 1_845_000),
            Managed(3, 1_858_000), Managed(4, 1_849_000), Managed(5, 1_853_000),
        };

        var r = TrendAssertions.NoUpwardSlope(samples, s => s.ManagedBytes, maxSlopePerSample: 20_000, "ManagedBytes");

        Assert.False(r.HasDrift, r.Detail);
    }

    [Fact]
    public void NoUpwardSlope_SustainedGrowth_DetectsDrift()
    {
        // +40_000 Bytes je Sample durchgängig — die Signatur eines pro Iteration retainten Puffers.
        var samples = Enumerable.Range(0, 8).Select(i => Managed(i, 1_800_000 + i * 40_000L)).ToArray();

        var r = TrendAssertions.NoUpwardSlope(samples, s => s.ManagedBytes, maxSlopePerSample: 20_000, "ManagedBytes");

        Assert.True(r.HasDrift, r.Detail);
        Assert.Contains("ManagedBytes", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoUpwardSlope_SingleMidSeriesSpike_NoDrift()
    {
        // Ein einzelner Ausreißer in der Mitte darf die Ausgleichsgerade nicht als Drift kippen.
        var samples = new[]
        {
            Managed(0, 1_850_000), Managed(1, 1_850_000), Managed(2, 3_000_000),
            Managed(3, 1_850_000), Managed(4, 1_850_000), Managed(5, 1_850_000),
        };

        var r = TrendAssertions.NoUpwardSlope(samples, s => s.ManagedBytes, maxSlopePerSample: 20_000, "ManagedBytes");

        Assert.False(r.HasDrift, r.Detail);
    }
}
