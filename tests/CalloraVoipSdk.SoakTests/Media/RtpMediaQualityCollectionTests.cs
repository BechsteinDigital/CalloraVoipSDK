using CalloraVoipSdk.InteropHarness.Media;

namespace CalloraVoipSdk.SoakTests.Media;

public sealed class RtpMediaQualityCollectionTests
{
    [Fact]
    public async Task RunAndCollectQualityAsync_ShortCall_CollectsSnapshotsWithDeliveredPackets()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: TimeSpan.FromSeconds(3), frameInterval: TimeSpan.FromMilliseconds(20));

        Assert.NotEmpty(snapshots);
        Assert.True(snapshots[^1].PacketsDelivered > 0, "Es müssen Pakete ausgeliefert worden sein.");
    }
}
