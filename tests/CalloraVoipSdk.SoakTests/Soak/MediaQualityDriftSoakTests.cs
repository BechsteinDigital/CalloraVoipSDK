using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class MediaQualityDriftSoakTests
{
    [Fact]
    public async Task LongCall_JitterDoesNotDrift()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: TimeSpan.FromSeconds(20), frameInterval: TimeSpan.FromMilliseconds(20));

        Assert.True(snapshots.Count >= 10, $"Zu wenige Snapshots: {snapshots.Count}");

        var jitter = TrendAssertions.NoUpwardDrift(
            snapshots, s => s.JitterMs, toleranceRatio: 0.50, metricName: "JitterMs");
        Assert.False(jitter.HasDrift, jitter.Detail);

        Assert.True(snapshots[^1].PacketsDelivered > 0, "Es müssen Pakete ausgeliefert worden sein.");
    }

    // Reproduziert F002: auf reinem UDP-Loopback (kein echter Verlust) MUSS UnrecoverableLoss 0 sein.
    // Aktuell blockiert durch F002 (late-angekommene Pakete werden fälschlich als UnrecoverableLoss
    // doppelt gezählt). Skip entfernen, sobald F002 gefixt ist — dann verifiziert dieser Test den Fix.
    [Fact(Skip = "F002 — Media-Defekt: Late-Drops fälschlich als UnrecoverableLoss, siehe docs/audit/INTEROP_SOAK_AUDIT.md")]
    public async Task LongCall_UnrecoverableLoss_IsZeroOnLoopback()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: TimeSpan.FromSeconds(20), frameInterval: TimeSpan.FromMilliseconds(20));

        Assert.Equal(0, snapshots[^1].PacketsUnrecoverableLoss);
    }
}
