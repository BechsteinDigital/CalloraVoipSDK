using Xunit;

namespace CalloraVoipSdk.InteropTests.Soak;

/// <summary>Validiert die deterministische Stufenauswahl des manuellen Kapazitätsbenchmarks.</summary>
public sealed class CalloraCapacityProfileTests
{
    /// <summary>Die Default-Grenzen bilden das dokumentierte strenge Qualitäts-Gate ab.</summary>
    [Fact]
    public void QualityGate_Defaults_AreStrict()
    {
        Assert.Equal(0.99, CalloraCapacityQualityGate.DefaultMinimumDeliveryRatio);
        Assert.Equal(40, CalloraCapacityQualityGate.DefaultMaximumP99IntervalMilliseconds);
        Assert.Equal(250, CalloraCapacityQualityGate.DefaultMaximumSilenceMilliseconds);
        Assert.Equal(0.01, CalloraCapacityQualityGate.DefaultMaximumPacketLossRatio);
        Assert.Equal(30, CalloraCapacityQualityGate.DefaultMaximumJitterMilliseconds);
    }

    /// <summary>Die automatisch erzeugte Leiter verdoppelt bis zum exakten Sicherheitslimit.</summary>
    [Fact]
    public void ParseLevels_DefaultLadder_ReachesExactCeiling()
    {
        var levels = CalloraCapacityProfile.ParseLevels(raw: null, start: 8, ceiling: 300);

        Assert.Equal(new[] { 8, 16, 32, 64, 128, 256, 300 }, levels);
    }

    /// <summary>Oberhalb von 1024 Calls verfeinert die automatische Leiter in 256er-Schritten.</summary>
    [Fact]
    public void ParseLevels_HighCapacityLadder_UsesFineGrainedCheckpoints()
    {
        var levels = CalloraCapacityProfile.ParseLevels(raw: null, start: 64, ceiling: 2500);

        Assert.Equal(
            new[] { 64, 128, 256, 512, 1024, 1280, 1536, 1792, 2048, 2304, 2500 },
            levels);
    }

    /// <summary>Eine explizite Leiter bleibt unverändert und erlaubt gezielte Verfeinerungsläufe.</summary>
    [Fact]
    public void ParseLevels_ExplicitAscendingValues_PreservesValues()
    {
        var levels = CalloraCapacityProfile.ParseLevels("12, 24,48", start: 8, ceiling: 256);

        Assert.Equal(new[] { 12, 24, 48 }, levels);
    }

    /// <summary>Mehrdeutige oder nicht monotone Stufen werden sichtbar abgelehnt.</summary>
    [Theory]
    [InlineData("8,8")]
    [InlineData("16,8")]
    [InlineData("8,nope")]
    [InlineData("0,8")]
    public void ParseLevels_InvalidExplicitValues_Throws(string raw)
    {
        Assert.Throws<InvalidOperationException>(
            () => CalloraCapacityProfile.ParseLevels(raw, start: 8, ceiling: 16));
    }
}
