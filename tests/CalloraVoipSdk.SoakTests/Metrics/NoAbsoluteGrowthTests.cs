using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Metrics;

/// <summary>
/// #283: die Statistik hinter dem Speicher-Gate. Der Prozess-Commit wächst treppenförmig, und eine
/// Ausgleichsgerade darüber misst die Stufenlage statt eines Lecks — auf identischem Commit einmal rot,
/// einmal grün. Die Reihen hier sind gemessen, nicht erfunden.
/// </summary>
public sealed class NoAbsoluteGrowthTests
{
    private static ResourceSample[] Series(params double[] megabytesOverStart)
    {
        const long baseline = 120_000_000; // realistischer Startsockel eines Soak-Prozesses
        return [.. megabytesOverStart.Select((mb, i) => new ResourceSample(
            SampleIndex: i,
            ManagedBytes: 0,
            PrivateMemoryBytes: baseline + (long)(mb * 1_000_000),
            WorkingSetBytes: 0,
            ThreadCount: 10,
            HandleCount: 0,
            FileDescriptorCount: 0,
            SocketDescriptorCount: 0))];
    }

    private const double Cap = 64_000_000;

    /// <summary>
    /// Die gemessene Reihe eines grünen Chaos-Laufs: Stufen, dann flach. Sie kippte das alte Steigungs-Gate
    /// auf CI und darf hier nicht als Leck gelten.
    /// </summary>
    [Fact]
    public void A_measured_staircase_that_flattens_is_not_drift()
    {
        var samples = Series(0, 0, 0.1, 0.2, 0.6, 0.6, 0.7, 2.0, 2.9, 5.2, 5.2, 5.3, 5.3, 5.3, 7.4, 7.4, 7.4, 7.4);

        var r = TrendAssertions.NoAbsoluteGrowth(samples, s => s.PrivateMemoryBytes, Cap, "PrivateMemoryBytes");

        Assert.False(r.HasDrift, r.Detail);
    }

    /// <summary>
    /// Warum eine Steigungsgrenze für diese Metrik die falsche Einheit ist: sie ist eine Gesamtwachstums-
    /// grenze, die ihre eigene Höhe verschweigt. 1 MB/Sample über 18 Samples heißt „höchstens 18 MB" — und
    /// dieselbe Reihe, feiner abgetastet, kommt plötzlich durch, ohne dass sich am Prozess etwas geändert
    /// hätte. Die gemessenen CI-Läufe lagen bei 23,5 und 24,1 MB, also über der versteckten Grenze; lokal
    /// waren es 7,4 MB.
    /// </summary>
    [Fact]
    public void A_slope_cap_is_a_hidden_total_cap_that_moves_with_the_sample_count()
    {
        // Gleiches Gesamtwachstum von 24 MB, einmal über 18 Samples, einmal über 36.
        var coarse = Series([.. Enumerable.Range(0, 18).Select(i => 24.0 * i / 17)]);
        var fine = Series([.. Enumerable.Range(0, 36).Select(i => 24.0 * i / 35)]);

        var coarseSlope = TrendAssertions.LeastSquaresSlope([.. coarse.Select(s => (double)s.PrivateMemoryBytes)]);
        var fineSlope = TrendAssertions.LeastSquaresSlope([.. fine.Select(s => (double)s.PrivateMemoryBytes)]);

        Assert.True(coarseSlope > 1_000_000, $"grob={coarseSlope:F0} — sollte die alte 1-MB-Grenze reißen");
        Assert.True(fineSlope < 1_000_000, $"fein={fineSlope:F0} — dieselben 24 MB, aber unter der Grenze");

        // Die neue Statistik bewertet beide gleich, weil sie das misst, was gemeint ist.
        Assert.False(TrendAssertions.NoAbsoluteGrowth(coarse, s => s.PrivateMemoryBytes, Cap, "m").HasDrift);
        Assert.False(TrendAssertions.NoAbsoluteGrowth(fine, s => s.PrivateMemoryBytes, Cap, "m").HasDrift);
    }

    /// <summary>Ein echtes Leck wächst weiter und sprengt die Grenze — das Gate greift noch.</summary>
    [Fact]
    public void Growth_past_the_cap_is_drift()
    {
        var samples = Series(0, 10, 20, 30, 40, 50, 60, 70, 80, 90);

        var r = TrendAssertions.NoAbsoluteGrowth(samples, s => s.PrivateMemoryBytes, Cap, "PrivateMemoryBytes");

        Assert.True(r.HasDrift, r.Detail);
    }

    /// <summary>
    /// Ein einzelner Ausreißer am Ende ist kein Leck. Gemessen im Concurrent-Soak: letzter Einzelwert 41 MB
    /// über dem Start, Median-Wachstum 2 MB — ein Endpunkt-Vergleich hätte hier Alarm geschlagen.
    /// </summary>
    [Fact]
    public void A_single_spike_at_the_end_does_not_count()
    {
        var samples = Series(0, 1, 2, 2, 2, 2, 2, 2, 2, 100);

        var r = TrendAssertions.NoAbsoluteGrowth(samples, s => s.PrivateMemoryBytes, Cap, "PrivateMemoryBytes");

        Assert.False(r.HasDrift, r.Detail);
    }

    /// <summary>Eine Reihe, die Speicher zurückgibt, ist nie Drift (RtpMediaLeak lag lokal bei −181 MB).</summary>
    [Fact]
    public void A_falling_series_is_not_drift()
    {
        var samples = Series(0, -20, -50, -90, -120, -150, -170, -181);

        var r = TrendAssertions.NoAbsoluteGrowth(samples, s => s.PrivateMemoryBytes, Cap, "PrivateMemoryBytes");

        Assert.False(r.HasDrift, r.Detail);
    }

    [Fact]
    public void Fewer_than_two_samples_is_not_drift()
    {
        var r = TrendAssertions.NoAbsoluteGrowth(Series(0), s => s.PrivateMemoryBytes, Cap, "PrivateMemoryBytes");

        Assert.False(r.HasDrift, r.Detail);
    }
}
