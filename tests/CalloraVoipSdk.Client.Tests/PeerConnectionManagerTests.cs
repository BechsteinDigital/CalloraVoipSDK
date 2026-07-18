using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The L2 peer registry (ADR-012 step 3 rest): <see cref="IWebRtcClient.Peers"/> tracks peers on creation
/// and drops them on disposal, so a multi-peer app can enumerate its live connections.
/// </summary>
public sealed class PeerConnectionManagerTests
{
    [Fact]
    public async Task Created_peers_are_tracked_and_disposed_peers_are_untracked()
    {
        var rtc = new WebRtcClient();
        Assert.Empty(rtc.Peers.Active);
        Assert.Equal(0, rtc.Peers.Count);

        var first = rtc.CreatePeer();
        var second = rtc.CreatePeer();

        Assert.Equal(2, rtc.Peers.Count);
        Assert.Contains(first, rtc.Peers.Active);
        Assert.Contains(second, rtc.Peers.Active);

        await first.DisposeAsync();

        Assert.Equal(1, rtc.Peers.Count);
        Assert.DoesNotContain(first, rtc.Peers.Active);
        Assert.Contains(second, rtc.Peers.Active);

        await second.DisposeAsync();

        Assert.Empty(rtc.Peers.Active);
    }

    [Fact]
    public async Task Active_is_a_point_in_time_snapshot()
    {
        var rtc = new WebRtcClient();

        var before = rtc.Peers.Active;
        await using var peer = rtc.CreatePeer();

        Assert.Empty(before);                 // the earlier snapshot is unaffected by later creation
        Assert.Single(rtc.Peers.Active);
    }
}
