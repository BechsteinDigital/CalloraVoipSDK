using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// BUNDLE track routing (ADR-010 B2b): an inbound RTP packet, once demultiplexed to its MID
/// (RFC 8843 §9.2), reaches the sink registered for that m-line; packets that cannot be associated or
/// whose m-line has no sink are dropped and counted.
/// </summary>
public sealed class BundledTrackRouterTests
{
    private const byte MidExtId = 3;

    private static BundledTrackRouter Router() => new(new BundledRtpDemultiplexer(
        MidExtId,
        new HashSet<string> { "audio", "video" },
        new Dictionary<int, string> { [111] = "audio", [96] = "video" }));

    private static RtpPacket Packet(uint ssrc, int pt, string? mid = null) => new()
    {
        Ssrc = ssrc,
        PayloadType = (byte)pt,
        HeaderExtension = mid is null ? null : RtpMidHeaderExtension.Encode(MidExtId, mid),
    };

    [Fact]
    public void Packets_are_routed_to_the_sink_of_their_m_line()
    {
        var router = Router();
        var audio = new List<RtpPacket>();
        var video = new List<RtpPacket>();
        router.RegisterTrack("audio", audio.Add);
        router.RegisterTrack("video", video.Add);

        Assert.True(router.DispatchInboundRtp(Packet(ssrc: 10, pt: 111, mid: "audio")));
        Assert.True(router.DispatchInboundRtp(Packet(ssrc: 20, pt: 96, mid: "video")));
        Assert.True(router.DispatchInboundRtp(Packet(ssrc: 10, pt: 111))); // same SSRC, no MID → latch

        Assert.Equal(2, audio.Count);
        Assert.Equal(20u, Assert.Single(video).Ssrc);
        Assert.Equal(0, router.DroppedPackets);
    }

    [Fact]
    public void An_undemuxable_packet_is_dropped_and_counted()
    {
        var router = Router();
        router.RegisterTrack("audio", _ => Assert.Fail("audio sink must not be hit"));

        Assert.False(router.DispatchInboundRtp(Packet(ssrc: 99, pt: 127))); // unknown PT, no MID, unlatched
        Assert.Equal(1, router.DroppedPackets);
    }

    [Fact]
    public void A_resolved_mid_with_no_registered_sink_is_dropped()
    {
        var router = Router();
        router.RegisterTrack("audio", _ => Assert.Fail("audio sink must not be hit"));

        // Resolves to "video" (PT 96), but no video sink is registered → dropped.
        Assert.False(router.DispatchInboundRtp(Packet(ssrc: 20, pt: 96, mid: "video")));
        Assert.Equal(1, router.DroppedPackets);
    }

    [Fact]
    public void Registering_the_same_mid_twice_throws()
    {
        var router = Router();
        router.RegisterTrack("audio", _ => { });

        Assert.Throws<InvalidOperationException>(() => router.RegisterTrack("audio", _ => { }));
    }

    [Fact]
    public void AddKnownMid_then_RegisterTrack_routes_a_live_added_mid_and_drops_it_cleanly_until_the_sink_exists()
    {
        // P3b live add: a packet for a MID the demux does not yet know is dropped (unknown MID rejected). After
        // AddKnownMid the MID demultiplexes; until the sink is registered it is dropped/counted (never crashes),
        // and once the sink is registered the packet is delivered — the exact known-mid → sink registration order.
        var router = Router();
        var vid2 = new List<RtpPacket>();

        // Before AddKnownMid: an explicit unknown MID is rejected by the demux (RFC 8843 §9.2).
        Assert.False(router.DispatchInboundRtp(Packet(ssrc: 30, pt: 96, mid: "vid2")));
        Assert.Equal(1, router.DroppedPackets);

        // Step 1 of the live add: make the MID known. Now it demultiplexes, but has no sink yet → clean drop.
        router.AddKnownMid("vid2");
        Assert.False(router.DispatchInboundRtp(Packet(ssrc: 31, pt: 96, mid: "vid2")));
        Assert.Equal(2, router.DroppedPackets);

        // Step 2: register the sink. The MID now delivers.
        router.RegisterTrack("vid2", vid2.Add);
        Assert.True(router.DispatchInboundRtp(Packet(ssrc: 32, pt: 96, mid: "vid2")));
        Assert.Equal(32u, Assert.Single(vid2).Ssrc);
        Assert.Equal(2, router.DroppedPackets); // unchanged — the delivered packet was not dropped.

        // Idempotent: re-adding a known MID is a no-op and does not throw.
        router.AddKnownMid("vid2");
    }

    [Fact]
    public void Unregistering_a_track_stops_delivery()
    {
        var router = Router();
        var audio = new List<RtpPacket>();
        router.RegisterTrack("audio", audio.Add);

        Assert.True(router.DispatchInboundRtp(Packet(10, 111, "audio")));
        Assert.True(router.UnregisterTrack("audio"));
        Assert.False(router.DispatchInboundRtp(Packet(10, 111))); // latched SSRC resolves, but no sink now

        Assert.Single(audio);
        Assert.Equal(1, router.DroppedPackets);
        Assert.False(router.UnregisterTrack("audio")); // already gone
    }
}
