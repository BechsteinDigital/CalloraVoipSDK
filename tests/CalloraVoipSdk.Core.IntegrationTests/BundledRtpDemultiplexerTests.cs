using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 8843 §9.2 BUNDLE demultiplexing (ADR-010 B2 routing brain): an inbound RTP packet is associated
/// with its m-line by SSRC latch → MID header extension → unambiguous payload type, and an explicit
/// unknown MID is dropped.
/// </summary>
public sealed class BundledRtpDemultiplexerTests
{
    private const byte MidExtId = 3;

    private static BundledRtpDemultiplexer Demuxer() => new(
        MidExtId,
        new HashSet<string> { "audio", "video" },
        new Dictionary<int, string> { [111] = "audio", [96] = "video" });

    private static RtpPacket Packet(uint ssrc, int pt, string? mid = null) => new()
    {
        Ssrc = ssrc,
        PayloadType = (byte)pt,
        HeaderExtension = mid is null ? null : RtpMidHeaderExtension.Encode(MidExtId, mid),
    };

    [Fact]
    public void Mid_extension_associates_and_latches_the_ssrc()
    {
        var demux = Demuxer();

        Assert.True(demux.TryResolveMid(Packet(ssrc: 100, pt: 96, mid: "video"), out var mid));
        Assert.Equal("video", mid);

        // A later packet on the same SSRC without a MID resolves via the learned association.
        Assert.True(demux.TryResolveMid(Packet(ssrc: 100, pt: 96), out var later));
        Assert.Equal("video", later);
    }

    [Fact]
    public void Latched_ssrc_wins_over_a_conflicting_payload_type()
    {
        var demux = Demuxer();
        demux.TryResolveMid(Packet(100, 96, "video"), out _); // SSRC 100 → video

        // Same SSRC, an audio payload type, no MID → still routes to the latched video m-line.
        Assert.True(demux.TryResolveMid(Packet(100, 111), out var mid));
        Assert.Equal("video", mid);
    }

    [Fact]
    public void Unknown_mid_is_dropped_and_not_latched()
    {
        var demux = Demuxer();

        Assert.False(demux.TryResolveMid(Packet(100, 96, "screenshare"), out var mid));
        Assert.Equal(string.Empty, mid);
        Assert.False(demux.TryResolveBySsrc(100, out _)); // the packet did not associate the SSRC
    }

    [Fact]
    public void Payload_type_associates_when_no_mid_is_present()
    {
        var demux = Demuxer();

        Assert.True(demux.TryResolveMid(Packet(200, 111), out var mid)); // audio PT, no MID
        Assert.Equal("audio", mid);
        Assert.True(demux.TryResolveBySsrc(200, out var latched));
        Assert.Equal("audio", latched);
    }

    [Fact]
    public void Undemuxable_packet_returns_false()
    {
        // Unknown PT, no MID, unlatched SSRC → cannot associate.
        Assert.False(Demuxer().TryResolveMid(Packet(300, 127), out var mid));
        Assert.Equal(string.Empty, mid);
    }

    [Fact]
    public void Mid_extension_id_zero_skips_mid_and_uses_payload_type()
    {
        var demux = new BundledRtpDemultiplexer(
            midExtensionId: 0, // MID extmap not negotiated
            new HashSet<string> { "audio", "video" },
            new Dictionary<int, string> { [96] = "video" });

        // The packet carries a (contradictory) MID header, but id 0 means it is not read → PT decides.
        Assert.True(demux.TryResolveMid(Packet(100, 96, "audio"), out var mid));
        Assert.Equal("video", mid);
    }

    [Fact]
    public void Resolve_by_ssrc_is_false_before_association()
    {
        Assert.False(Demuxer().TryResolveBySsrc(999, out var mid));
        Assert.Equal(string.Empty, mid);
    }

    [Fact]
    public void Add_known_mid_accepts_and_latches_a_mid_call_track()
    {
        var demux = Demuxer();

        // Before the track is added, its MID is outside the demux boundary → dropped, not latched.
        Assert.False(demux.TryResolveMid(Packet(ssrc: 400, pt: 96, mid: "screenshare"), out _));
        Assert.False(demux.TryResolveBySsrc(400, out _));

        demux.AddKnownMid("screenshare");

        // Now a packet with the newly negotiated MID (and a fresh SSRC) is accepted and latches.
        Assert.True(demux.TryResolveMid(Packet(ssrc: 401, pt: 96, mid: "screenshare"), out var mid));
        Assert.Equal("screenshare", mid);
        Assert.True(demux.TryResolveBySsrc(401, out var latched));
        Assert.Equal("screenshare", latched);
    }

    [Fact]
    public void Unadded_mid_stays_outside_the_demux_boundary()
    {
        var demux = Demuxer();
        demux.AddKnownMid("screenshare");

        // A different, never-added MID is still rejected — the boundary is not weakened.
        Assert.False(demux.TryResolveMid(Packet(ssrc: 500, pt: 96, mid: "datachannel"), out var mid));
        Assert.Equal(string.Empty, mid);
        Assert.False(demux.TryResolveBySsrc(500, out _));
    }

    [Fact]
    public void Add_known_mid_is_idempotent()
    {
        var demux = Demuxer();

        demux.AddKnownMid("screenshare");
        demux.AddKnownMid("screenshare"); // duplicate must not throw
        demux.AddKnownMid("video");       // already known from construction must not throw

        Assert.True(demux.TryResolveMid(Packet(ssrc: 600, pt: 96, mid: "screenshare"), out var mid));
        Assert.Equal("screenshare", mid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Add_known_mid_rejects_null_or_empty(string? mid)
    {
        Assert.Throws<ArgumentException>(() => Demuxer().AddKnownMid(mid!));
    }

    // ---- RFC 8853 recv-side simulcast RID resolution (4.7.0 slice 1) ----
    // TryResolveRid latches SSRC→RID exactly as TryResolveMid latches SSRC→MID. No dispatch path calls it
    // yet, so these tests exercise the resolution capability in isolation; the demux boundary is unchanged.

    private const byte RidExtId = 4;

    private static BundledRtpDemultiplexer SimulcastDemuxer() => new(
        MidExtId,
        new HashSet<string> { "audio", "video" },
        new Dictionary<int, string> { [111] = "audio", [96] = "video" },
        ridExtensionId: RidExtId);

    private static RtpPacket RidPacket(uint ssrc, int pt, string? rid = null) => new()
    {
        Ssrc = ssrc,
        PayloadType = (byte)pt,
        HeaderExtension = rid is null ? null : RtpRidHeaderExtension.Encode(RidExtId, rid),
    };

    [Fact]
    public void Rid_extension_resolves_and_latches_the_ssrc()
    {
        var demux = SimulcastDemuxer();

        Assert.True(demux.TryResolveRid(RidPacket(ssrc: 100, pt: 96, rid: "hi"), out var rid));
        Assert.Equal("hi", rid);
    }

    [Fact]
    public void Latched_rid_resolves_a_later_packet_without_the_extension()
    {
        var demux = SimulcastDemuxer();
        demux.TryResolveRid(RidPacket(ssrc: 100, pt: 96, rid: "lo"), out _); // first packet carries the RID

        // Browsers omit the RID extension after the first packets of an encoding → resolve from the latch.
        Assert.True(demux.TryResolveRid(RidPacket(ssrc: 100, pt: 96), out var rid));
        Assert.Equal("lo", rid);
    }

    [Fact]
    public void Rid_extension_id_null_returns_false()
    {
        // No simulcast encoding negotiated (RidExtensionId null) → RID resolution is off entirely.
        var demux = Demuxer(); // constructed without a RID extension id

        Assert.False(demux.TryResolveRid(RidPacket(ssrc: 100, pt: 96, rid: "hi"), out var rid));
        Assert.Equal(string.Empty, rid);
    }

    [Fact]
    public void Unlatched_ssrc_without_a_rid_extension_returns_false()
    {
        // An SSRC never seen with a RID and no RID on this packet → cannot resolve an encoding.
        Assert.False(SimulcastDemuxer().TryResolveRid(RidPacket(ssrc: 300, pt: 96), out var rid));
        Assert.Equal(string.Empty, rid);
    }
}
