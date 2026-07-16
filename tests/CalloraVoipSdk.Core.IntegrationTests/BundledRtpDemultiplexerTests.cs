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
}
