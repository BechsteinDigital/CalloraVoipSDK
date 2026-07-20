using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Stats slice 1: the <see cref="WebRtcStats"/> snapshot surface + <see cref="BitrateMeter"/>. Transport
/// counters and derived bitrates are populated; not-yet-wired metrics read null (honest, never fabricated).
/// </summary>
public sealed class WebRtcStatsTests
{
    [Fact]
    public void Bitrate_meter_returns_null_on_first_sample_then_the_rate()
    {
        var meter = new BitrateMeter();
        var oneSecond = TimeSpan.FromSeconds(1).Ticks;

        Assert.Null(meter.Sample(0, 0));                 // first sample only sets the baseline
        Assert.Equal(8000d, meter.Sample(1000, oneSecond));   // +1000 bytes in 1 s = 8000 bit/s
        Assert.Null(meter.Sample(2000, oneSecond));      // non-positive interval → null
    }

    [Fact]
    public void Rate_meter_returns_null_on_first_sample_then_the_rate()
    {
        var meter = new RateMeter();
        var oneSecond = TimeSpan.FromSeconds(1).Ticks;

        Assert.Null(meter.Sample(0, 0));                 // baseline only
        Assert.Equal(30d, meter.Sample(30, oneSecond));  // +30 frames in 1 s = 30 fps
        Assert.Null(meter.Sample(60, oneSecond));        // non-positive interval → null
    }

    [Fact]
    public async Task GetStats_on_a_fresh_peer_reports_state_with_zero_counters_and_null_unwired_metrics()
    {
        var rtc = new WebRtcClient();
        await using var peer = rtc.CreatePeer();

        var stats = peer.GetStats();

        Assert.Equal(PeerConnectionState.New, stats.ConnectionState);
        Assert.Equal(0, stats.PacketsSent);
        Assert.Equal(0, stats.BytesReceived);
        Assert.Null(stats.OutgoingBitrateBps);           // no session yet
        Assert.Null(stats.PacketLoss);                   // RTCP quality — later slice
        Assert.Null(stats.JitterMs);                     // no inbound media → no clock established yet
        Assert.Null(stats.FramesPerSecond);              // no video track yet
        Assert.Null(stats.KeyFrames);                    // no video track yet
        Assert.Null(stats.FramesDropped);                // frame-drop accounting — deferred
        Assert.Null(stats.NackCount);                    // bundle video feedback — deferred
        Assert.Equal("new", stats.IceState);             // derived from connectivity (S2)
        Assert.Null(stats.SelectedLocalCandidate);       // no bound endpoint yet
        Assert.Null(stats.SelectedRemoteCandidate);
        Assert.Null(stats.AvailableOutgoingBitrateBps);  // transport-cc — later slice
    }
}
