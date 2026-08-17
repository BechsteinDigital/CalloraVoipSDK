using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L2 — #223 / ADR-068: the opaque-video-frames switch, from the peer's transport policy down to the payload
/// format the media path actually resolves. The opaque payload format itself is pinned in
/// <see cref="OpaqueVideoPayloadFormatTests"/>; what is under test here is that the policy <em>arrives</em> —
/// on the tracks the session factory builds, on both send/receive halves of a track, and on a simulcast receive
/// lane that is built lazily long after the track was configured. A lane that silently fell back to the
/// clear-media pair would read ciphertext as codec syntax, which is the defect the switch exists to prevent.
/// </summary>
public sealed class OpaqueVideoSwitchTests
{
    private const byte MidExtId = 3;
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint RtpTimestamp = 90000;
    private const int ReorderDepth = 32;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    // ── the peer policy reaches the built track config ───────────────────────────────────────

    /// <summary>
    /// The policy is a property of the peer, not of the SDP — no m-line attribute carries it — so the factory
    /// takes it as an argument and stamps it on every video track it builds.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_session_factory_stamps_the_peer_policy_on_a_video_track(bool opaque)
    {
        var (local, remote) = VideoExchange();

        var config = WebRtcSessionFactory.TryBuildVideoTrack(
            local.Media.First(m => m.MediaType == "video"), remote, new HashSet<uint>(),
            NullLoggerFactory.Instance, opaqueVideoFrames: opaque);

        Assert.NotNull(config);
        Assert.Equal(opaque, config!.OpaqueVideoFrames);
    }

    /// <summary>
    /// The simulcast branch of the factory returns a different config object than the single-stream branch, so it
    /// carries the policy separately — a simulcast peer must not lose it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_session_factory_stamps_the_peer_policy_on_a_simulcast_video_track(bool opaque)
    {
        var (local, remote) = SimulcastVideoExchange();

        var config = WebRtcSessionFactory.TryBuildVideoTrack(
            local.Media.First(m => m.MediaType == "video"), remote, new HashSet<uint>(),
            NullLoggerFactory.Instance, opaqueVideoFrames: opaque);

        Assert.NotNull(config);
        Assert.NotEmpty(config!.Encodings); // the simulcast branch really was taken
        Assert.Equal(opaque, config.OpaqueVideoFrames);
    }

    // ── the config reaches both halves of the media path ─────────────────────────────────────

    /// <summary>
    /// The end-to-end point of the switch at track level: an H.264 track built opaque carries a frame of pure
    /// ciphertext through packetisation, the outbound pipeline and reassembly byte-identically, and claims no key
    /// frame. The clear-media pair cannot do this — it parses Annex-B on send and dispatches on NAL types on
    /// receive — which the contrast test below pins.
    /// </summary>
    [Fact]
    public async Task An_opaque_h264_track_carries_a_ciphertext_frame_byte_identically()
    {
        var frame = RandomFrame(9_000, seed: 223);

        var sent = new List<RtpPacket>();
        var outbound = OutboundOver(new DiscardSender());
        outbound.PacketSent += sent.Add;
        outbound.InstallOutboundKey(new SrtpContext(Material())); // so sends are not fail-closed (K1)

        using var sender = VideoTrack(outbound, "H264", opaqueFrames: true);
        await sender.SendFrameAsync(frame, RtpTimestamp);
        Assert.True(sent.Count > 1); // the frame fragmented; every fragment header is ours, not content

        byte[]? received = null;
        var keyFrameClaims = new List<bool>();
        using var receiver = VideoTrack(OutboundOver(new DiscardSender()), "H264", opaqueFrames: true);
        receiver.FrameReceived += (f, _) => { received = f.Payload; keyFrameClaims.Add(f.IsKeyFrame); };
        foreach (var packet in sent)
            receiver.OnRtpPacket(packet);

        Assert.Equal(frame, received);
        Assert.Equal([false], keyFrameClaims); // no claim about content that was never read
        Assert.Equal(0, receiver.KeyFrames);
    }

    /// <summary>
    /// The contrast that gives the test above its meaning, and #223's H.264 finding at track level: the same frame
    /// on a clear-media track never reaches the wire at all. Its packetiser runs the Annex-B parser to find NAL
    /// boundaries, ciphertext has none, and it refuses the frame — "H.264 fails closed on principle", not merely
    /// with a wrong flag. Nothing is sent, so the switch is what makes an encrypted video track possible at all.
    /// </summary>
    [Fact]
    public async Task A_clear_media_h264_track_refuses_the_same_ciphertext_frame()
    {
        var frame = RandomFrame(9_000, seed: 223);

        var sent = new List<RtpPacket>();
        var outbound = OutboundOver(new DiscardSender());
        outbound.PacketSent += sent.Add;
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        using var sender = VideoTrack(outbound, "H264", opaqueFrames: false);

        await Assert.ThrowsAsync<ArgumentException>(async () => await sender.SendFrameAsync(frame, RtpTimestamp));
        Assert.Empty(sent);
    }

    /// <summary>
    /// A simulcast receive lane is built on first sighting of its RID, long after the track was configured
    /// (browsers stamp the RID only on an encoding's first packets). It must resolve the same policy: with a VP8
    /// payload whose first frame byte reads as "key frame" in the clear format, the opaque lane makes no claim.
    /// </summary>
    [Fact]
    public void A_lazily_built_simulcast_receive_lane_keeps_the_opaque_policy()
    {
        using var opaqueTrack = SimulcastTrack("VP8", opaqueFrames: true);
        var opaqueClaims = new List<bool>();
        opaqueTrack.FrameReceived += (frame, _) => opaqueClaims.Add(frame.IsKeyFrame);

        using var clearTrack = SimulcastTrack("VP8", opaqueFrames: false);
        var clearClaims = new List<bool>();
        clearTrack.FrameReceived += (frame, _) => clearClaims.Add(frame.IsKeyFrame);

        // 0x00 as the frame's first byte = P bit clear = a key frame to the clear-media VP8 depacketiser
        // (RFC 7741 §4.3 → RFC 6386 §9.1). Under encryption that byte is ciphertext, so the claim is a coin flip.
        var packet = Vp8Packet(ssrc: 0x1111, seq: 1000, frameByte: 0x00);
        opaqueTrack.OnRtpPacket(packet, rid: "h");
        clearTrack.OnRtpPacket(packet, rid: "h");

        Assert.Equal([false], opaqueClaims);  // lazily built lane resolved the opaque pair
        Assert.Equal([true], clearClaims);    // and the clear path is unchanged
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private static BundledVideoTrack VideoTrack(BundledOutboundPipeline outbound, string codec, bool opaqueFrames) =>
        new("video", codec, VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            outbound, ReorderDepth, NullLoggerFactory.Instance, opaqueFrames: opaqueFrames);

    private static BundledVideoTrack SimulcastTrack(string codec, bool opaqueFrames) =>
        new("video", codec, VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            ["h", "l"], OutboundOver(new DiscardSender()), ReorderDepth, NullLoggerFactory.Instance,
            opaqueFrames: opaqueFrames);

    private static BundledOutboundPipeline OutboundOver(IBundledDatagramSender sender)
    {
        var pipeline = new BundledOutboundPipeline(new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video", new BundledOutboundTrack(
            VideoSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "video"),
            initialSequenceNumber: 1000, initialTimestamp: RtpTimestamp));
        return pipeline;
    }

    private static SrtpKeyMaterial Material() => new(MasterKey, MasterSalt, SrtpCryptoSuite.AesCm128HmacSha1_80);

    // A single-packet VP8 frame: the minimal 1-byte payload descriptor (RFC 7741 §4.2, S=1) followed by one
    // frame byte, with the marker closing the frame.
    private static RtpPacket Vp8Packet(uint ssrc, ushort seq, byte frameByte) => new()
    {
        Ssrc = ssrc,
        PayloadType = VideoPayloadType,
        SequenceNumber = seq,
        Timestamp = RtpTimestamp,
        Marker = true,
        Payload = new byte[] { 0x10, frameByte },
    };

    // A negotiated single-stream H.264 video m-line pair, both sides send-recv (so a sending track is built).
    private static (SdpSessionDescription Local, SdpSessionDescription Remote) VideoExchange()
    {
        const string sdp =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 6002 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\na=mid:1\r\na=sendrecv\r\n";
        return (new SdpSessionParser().Parse(sdp), new SdpSessionParser().Parse(sdp));
    }

    // The same pair with confirmed simulcast: we offer two send RIDs, the peer answers recv for both and echoes
    // the RID header extension, so the factory takes its simulcast branch (RFC 8853/8852).
    private static (SdpSessionDescription Local, SdpSessionDescription Remote) SimulcastVideoExchange()
    {
        const string head =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 6002 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\na=mid:1\r\na=sendrecv\r\n" +
            "a=extmap:4 urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id\r\n";
        return (
            new SdpSessionParser().Parse(head + "a=rid:h send\r\na=rid:l send\r\na=simulcast:send h;l\r\n"),
            new SdpSessionParser().Parse(head + "a=rid:h recv\r\na=rid:l recv\r\na=simulcast:recv h;l\r\n"));
    }

    private static byte[] RandomFrame(int length, int seed)
    {
        var frame = new byte[length];
        new Random(seed).NextBytes(frame);
        return frame;
    }

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
