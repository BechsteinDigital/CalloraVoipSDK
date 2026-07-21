using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class MediaQualityDriftSoakTests
{
    [Fact, Trait("Category", "SoakShort")]
    public Task LongCall_JitterDoesNotDrift_Short() => RunJitterAsync(SoakProfile.Short);

    [Fact, Trait("Category", "SoakLong")]
    public Task LongCall_JitterDoesNotDrift_Long() => RunJitterAsync(SoakProfile.Long);

    private static async Task RunJitterAsync(SoakProfile profile)
    {
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: profile.Duration, frameInterval: TimeSpan.FromMilliseconds(20));

        // Short: kurze Laufdauer — Startup-Jitter dominiert, Trend nicht aussagekräftig.
        // Smoke prüft nur: Mechanismus läuft und Pakete kommen an.
        var isShort = profile.Duration.TotalSeconds <= 5;
        var minSnapshots = isShort ? 3 : 10;
        Assert.True(snapshots.Count >= minSnapshots, $"Zu wenige Snapshots: {snapshots.Count}");

        if (!isShort)
        {
            var jitter = TrendAssertions.NoUpwardDrift(
                snapshots, s => s.JitterMs, toleranceRatio: 0.50, metricName: "JitterMs");
            Assert.False(jitter.HasDrift, jitter.Detail);
        }

        Assert.True(snapshots[^1].PacketsDelivered > 0, "Es müssen Pakete ausgeliefert worden sein.");
    }

    // Reproduziert F002: auf reinem UDP-Loopback (kein echter Verlust) MUSS UnrecoverableLoss 0 sein.
    // Aktuell blockiert durch F002 (late-angekommene Pakete werden fälschlich als UnrecoverableLoss
    // doppelt gezählt). Skip entfernen, sobald F002 gefixt ist — dann verifiziert dieser Test den Fix.
    [Fact(Skip = "F002 — Media-Defekt: Late-Drops fälschlich als UnrecoverableLoss, siehe docs/audit/INTEROP_SOAK_AUDIT.md"), Trait("Category", "SoakLong")]
    public async Task LongCall_UnrecoverableLoss_IsZeroOnLoopback()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: TimeSpan.FromSeconds(20), frameInterval: TimeSpan.FromMilliseconds(20));

        Assert.Equal(0, snapshots[^1].PacketsUnrecoverableLoss);
    }
}
