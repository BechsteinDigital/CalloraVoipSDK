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

    // ---- RFC 8853 recv-side simulcast: a RID-aware sink receives the resolved a=rid (4.7.0 slice 2) ----

    private const byte RidExtId = 4;

    private static BundledTrackRouter SimulcastRouter() => new(new BundledRtpDemultiplexer(
        MidExtId,
        new HashSet<string> { "audio", "video" },
        new Dictionary<int, string> { [111] = "audio", [96] = "video" },
        ridExtensionId: RidExtId));

    private static RtpPacket RidPacket(uint ssrc, int pt, string? mid = null, string? rid = null)
    {
        RtpExtension? ext = (mid, rid) switch
        {
            (not null, null) => RtpMidHeaderExtension.Encode(MidExtId, mid),
            (null, not null) => RtpRidHeaderExtension.Encode(RidExtId, rid),
            _ => null,
        };
        return new RtpPacket { Ssrc = ssrc, PayloadType = (byte)pt, HeaderExtension = ext };
    }

    [Fact]
    public void With_no_rid_extension_negotiated_the_video_sink_receives_a_null_rid()
    {
        // RidDemuxEnabled is false (the base Router() has no ridExtensionId) → no RID resolution runs and the
        // dispatch stays byte-identical to the pre-simulcast path: the sink always sees rid null.
        var router = Router();
        var seen = new List<string?>();
        router.RegisterTrack("video", (_, rid) => seen.Add(rid));

        Assert.True(router.DispatchInboundRtp(Packet(ssrc: 20, pt: 96, mid: "video")));
        Assert.Null(Assert.Single(seen));
    }

    [Fact]
    public void With_a_rid_extension_the_video_sink_receives_the_resolved_rid()
    {
        var router = SimulcastRouter();
        var seen = new List<(uint Ssrc, string? Rid)>();
        router.RegisterTrack("video", (p, rid) => seen.Add((p.Ssrc, rid)));

        // Two encodings under the one video MID (routed by MID ext) each carry their RID ext on first sighting.
        Assert.True(router.DispatchInboundRtp(RidPacket(ssrc: 0x1111, pt: 96, mid: "video")));       // latch MID
        Assert.True(router.DispatchInboundRtp(RidPacket(ssrc: 0x1111, pt: 96, rid: "h")));           // RID "h"
        Assert.True(router.DispatchInboundRtp(RidPacket(ssrc: 0x2222, pt: 96, mid: "video")));       // latch MID
        Assert.True(router.DispatchInboundRtp(RidPacket(ssrc: 0x2222, pt: 96, rid: "l")));           // RID "l"
        // Later RID-less packet on a latched SSRC resolves the RID from the SSRC→RID latch (the hot path).
        Assert.True(router.DispatchInboundRtp(RidPacket(ssrc: 0x1111, pt: 96)));                     // resolves "h"

        // Every RID-resolved packet on 0x1111 is "h"; on 0x2222 is "l" — no cross-talk in the SSRC→RID latch.
        Assert.All(seen.Where(s => s is { Ssrc: 0x1111u, Rid: not null }), s => Assert.Equal("h", s.Rid));
        Assert.All(seen.Where(s => s is { Ssrc: 0x2222u, Rid: not null }), s => Assert.Equal("l", s.Rid));
        Assert.Equal(2, seen.Count(s => s is { Ssrc: 0x1111u, Rid: "h" })); // the RID packet + the latched hot-path packet
    }

    [Fact]
    public void A_rid_unaware_back_compat_sink_still_receives_every_packet()
    {
        // The single-arg RegisterTrack overload wraps a RID-unaware sink — it must keep working unchanged even
        // when a RID extension is negotiated (audio, or non-simulcast video).
        var router = SimulcastRouter();
        var audio = new List<RtpPacket>();
        router.RegisterTrack("audio", audio.Add);

        Assert.True(router.DispatchInboundRtp(RidPacket(ssrc: 10, pt: 111, mid: "audio")));
        Assert.True(router.DispatchInboundRtp(RidPacket(ssrc: 10, pt: 111))); // latched
        Assert.Equal(2, audio.Count);
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
