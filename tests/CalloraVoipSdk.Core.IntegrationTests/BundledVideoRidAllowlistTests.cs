using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Inbound RID demultiplexing is bounded by an allowlist of the RIDs actually negotiated for receive
/// (RFC 8853/8852), not only by the lane cap (#161 P3-15, the remainder of #154). The cap bounds what an
/// unknown RID can cost — a depacketiser and a reorder buffer each — but says nothing about whether the RID
/// is entitled to a lane at all, and the two are not substitutes. With nothing negotiated the allowlist is
/// empty and every RID is admitted, exactly as before.
/// </summary>
public sealed class BundledVideoRidAllowlistTests
{
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint RtpTimestamp = 90000;
    private const int ReorderDepth = 32;

    private static BundledVideoTrack Track(IReadOnlyList<string>? receiveRids) =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            new[] { "h", "l" }, Outbound(), ReorderDepth, NullLoggerFactory.Instance, receiveRids);

    private static BundledOutboundPipeline Outbound() =>
        new(new RtpPacketCodec(), new DiscardSender(), NullLogger<BundledOutboundPipeline>.Instance);

    private static RtpPacket Packet(uint ssrc, ushort seq, byte payloadByte) => new()
    {
        Ssrc = ssrc,
        PayloadType = VideoPayloadType,
        SequenceNumber = seq,
        Timestamp = RtpTimestamp,
        Marker = true,
        Payload = Vp8(payloadByte),
    };

    private static byte[] Vp8(byte body) => [0x10, body]; // S=1 descriptor + one body byte

    [Fact]
    public void A_rid_outside_the_negotiated_receive_set_gets_no_lane()
    {
        using var track = Track(["h", "l"]);
        var received = new List<string?>();
        track.FrameReceived += (_, rid) => received.Add(rid);

        track.OnRtpPacket(Packet(0x1111, 1000, 0xAA), rid: "h");
        track.OnRtpPacket(Packet(0x3333, 2000, 0xCC), rid: "x"); // never negotiated
        track.OnRtpPacket(Packet(0x2222, 40, 0xBB), rid: "l");

        Assert.Equal(["h", "l"], received);
    }

    [Fact]
    public void A_flood_of_unnegotiated_rids_never_reaches_the_lane_cap()
    {
        using var track = Track(["h"]);
        var received = new List<string?>();
        track.FrameReceived += (_, rid) => received.Add(rid);

        // Far more distinct RIDs than the lane cap: with an allowlist none of them is entitled to a lane, so
        // the cap is never the thing doing the work — and the negotiated encoding still gets through after.
        for (var i = 0; i < 100; i++)
            track.OnRtpPacket(Packet((uint)(0x9000 + i), (ushort)(3000 + i), 0xDD), rid: $"spoof{i}");

        track.OnRtpPacket(Packet(0x1111, 1000, 0xAA), rid: "h");

        Assert.Equal(["h"], received);
    }

    [Fact]
    public void With_nothing_negotiated_every_rid_is_still_admitted()
    {
        using var track = Track(receiveRids: null);
        var received = new List<string?>();
        track.FrameReceived += (_, rid) => received.Add(rid);

        track.OnRtpPacket(Packet(0x1111, 1000, 0xAA), rid: "h");
        track.OnRtpPacket(Packet(0x3333, 2000, 0xCC), rid: "x");

        Assert.Equal(["h", "x"], received);
    }

    [Fact]
    public void The_rid_less_default_lane_is_unaffected_by_the_allowlist()
    {
        using var track = Track(["h"]);
        var received = new List<string?>();
        track.FrameReceived += (_, rid) => received.Add(rid);

        // A non-simulcast sender stamps no RID at all; that stream is not something an allowlist may drop.
        track.OnRtpPacket(Packet(0x4444, 500, 0xEE), rid: null);

        Assert.Equal([(string?)null], received);
    }

    // ── the negotiated set comes off the SDP pair ────────────────────────────────────────────

    [Fact]
    public void The_receive_allowlist_is_the_recv_rids_the_peer_also_announces_as_send()
    {
        // We offer to receive two encodings; the peer answers that it sends exactly those.
        var local = new SdpSessionParser().Parse(
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 6002 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\na=sendrecv\r\n" +
            "a=extmap:4 urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id\r\n" +
            "a=rid:h recv\r\na=rid:l recv\r\na=simulcast:recv h;l\r\n");
        var remote = new SdpSessionParser().Parse(
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 6002 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\na=sendrecv\r\n" +
            "a=extmap:4 urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id\r\n" +
            "a=rid:h send\r\na=rid:l send\r\na=simulcast:send h;l\r\n");

        var config = WebRtcSessionFactory.TryBuildVideoTrack(
            local.Media.First(m => m.MediaType == "video"), remote, new HashSet<uint>(), NullLoggerFactory.Instance,
            opaqueVideoFrames: false);

        Assert.NotNull(config);
        Assert.Equal(["h", "l"], config!.ReceiveRids);
    }

    [Fact]
    public void A_peer_that_announces_no_simulcast_leaves_the_allowlist_empty()
    {
        // Our own recv offer alone must not become an allowlist: a peer sending a plain stream (or one whose
        // RIDs we cannot see) would otherwise have its packets dropped.
        var local = new SdpSessionParser().Parse(
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 6002 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\na=sendrecv\r\n" +
            "a=extmap:4 urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id\r\n" +
            "a=rid:h recv\r\na=simulcast:recv h\r\n");
        var remote = new SdpSessionParser().Parse(
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 6002 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\na=sendrecv\r\n");

        var config = WebRtcSessionFactory.TryBuildVideoTrack(
            local.Media.First(m => m.MediaType == "video"), remote, new HashSet<uint>(), NullLoggerFactory.Instance,
            opaqueVideoFrames: false);

        Assert.NotNull(config);
        Assert.Empty(config!.ReceiveRids);
    }

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
