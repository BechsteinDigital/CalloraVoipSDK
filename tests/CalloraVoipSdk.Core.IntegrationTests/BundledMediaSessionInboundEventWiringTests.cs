using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The inbound-event wiring collaborator (4.7.0 slice 3) subscribes one video track's frame events onto the
/// session's raise delegates so that EXACTLY ONE surface fires per frame (the M-2 double-delivery fix): a
/// RID-tagged frame (a demultiplexed simulcast layer, RFC 8853) fires ONLY the per-layer
/// <c>VideoLayerFrameReceived</c>; a RID-less frame (primary/default stream) fires the mid-less
/// <c>VideoFrameReceived</c> (primary track only) and the mid-tagged <c>VideoTrackFrameReceived</c> — never the
/// per-layer surface — so the non-simulcast path is byte-identical to the pre-slice behaviour.
/// </summary>
public sealed class BundledMediaSessionInboundEventWiringTests
{
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const int ReorderDepth = 32;

    [Fact]
    public void A_rid_tagged_layer_frame_fires_only_the_per_layer_surface()
    {
        var (wiring, sink) = Wiring();
        using var track = SimulcastReceiveTrack();
        wiring.WireVideoTrackEvents("cam", track, isPrimary: true);

        // A RID-tagged single-packet keyframe on the "h" encoding.
        track.OnRtpPacket(Packet(ssrc: 0x1111, seq: 1000, marker: true, payload: new byte[] { 0xAA }), rid: "h");

        // Exactly one surface fired: the per-layer one, with the correct (mid, rid).
        var layer = Assert.Single(sink.Layer);
        Assert.Equal(("cam", "h"), (layer.Mid, layer.Rid));
        Assert.Equal(new byte[] { 0xAA }, layer.Frame);
        // The RID-less surfaces stayed silent — no double-delivery of the layer frame.
        Assert.Empty(sink.PrimaryFrame);
        Assert.Empty(sink.Track);
    }

    [Fact]
    public void A_rid_less_primary_frame_fires_the_mid_less_and_mid_tagged_surfaces_but_not_the_layer_surface()
    {
        var (wiring, sink) = Wiring();
        using var track = NonSimulcastTrack();
        wiring.WireVideoTrackEvents("video", track, isPrimary: true);

        track.OnRtpPacket(Packet(ssrc: VideoSsrc, seq: 1000, marker: true, payload: new byte[] { 0x7F }));

        // The primary RID-less frame fires the mid-less VideoFrameReceived AND the mid-tagged VideoTrackFrameReceived.
        Assert.Equal(new byte[] { 0x7F }, Assert.Single(sink.PrimaryFrame));
        var track1 = Assert.Single(sink.Track);
        Assert.Equal("video", track1.Mid);
        Assert.Equal(new byte[] { 0x7F }, track1.Frame);
        // The per-layer surface stayed silent for a RID-less frame.
        Assert.Empty(sink.Layer);
    }

    [Fact]
    public void A_non_primary_rid_less_track_fires_only_the_mid_tagged_surface()
    {
        // A live-added (non-primary) track never drives the mid-less VideoFrameReceived facade — only the
        // mid-tagged one, so N tracks stay distinguishable and the primary facade stays pinned to the ctor track.
        var (wiring, sink) = Wiring();
        using var track = NonSimulcastTrack();
        wiring.WireVideoTrackEvents("scr", track, isPrimary: false);

        track.OnRtpPacket(Packet(ssrc: VideoSsrc, seq: 1000, marker: true, payload: new byte[] { 0x33 }));

        Assert.Empty(sink.PrimaryFrame);                 // non-primary → no mid-less facade
        Assert.Equal("scr", Assert.Single(sink.Track).Mid);
        Assert.Empty(sink.Layer);
    }

    [Fact]
    public void A_key_frame_request_reaches_the_session_tagged_with_the_mid_it_arrived_on()
    {
        // The mid is what a forwarding layer keys its upstream sources by, so it has to survive the hop from
        // the track to the session; the track alone knows which m-line it is (#227).
        var (wiring, sink) = Wiring();
        using var track = NonSimulcastTrack();
        wiring.WireVideoTrackEvents("scr", track, isPrimary: false);

        track.OnRtcpPackets([new RtcpPictureLossIndication { SenderSsrc = 0xD0D0D0D0, MediaSsrc = VideoSsrc }]);

        var attributed = Assert.Single(sink.KeyFrameStream);
        Assert.Equal("scr", attributed.Mid);
        Assert.Equal(VideoSsrc, attributed.Request.MediaSsrc);
        Assert.Equal(1, sink.KeyFrameRequested);   // the mid-less surface still fires alongside it
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private sealed class Sink
    {
        public List<byte[]> PrimaryFrame { get; } = [];
        public List<(string Mid, byte[] Frame)> Track { get; } = [];
        public List<(string Mid, string Rid, byte[] Frame)> Layer { get; } = [];
        public int KeyFrameRequested;
        public List<(string Mid, VideoKeyFrameRequest Request)> KeyFrameStream { get; } = [];
    }

    private static (BundledMediaSessionInboundEventWiring Wiring, Sink Sink) Wiring()
    {
        var sink = new Sink();
        var wiring = new BundledMediaSessionInboundEventWiring(
            frame => sink.PrimaryFrame.Add(frame.Payload),
            (mid, frame) => sink.Track.Add((mid, frame.Payload)),
            (mid, rid, frame) => sink.Layer.Add((mid, rid, frame.Payload)),
            () => sink.KeyFrameRequested++,
            (mid, request) => sink.KeyFrameStream.Add((mid, request)),
            (_, _) => { },
            NullLogger<BundledMediaSessionInboundEventWiringTests>.Instance);
        return (wiring, sink);
    }

    private static BundledVideoTrack SimulcastReceiveTrack() =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            new[] { "h", "l" }, Outbound(), ReorderDepth, NullLoggerFactory.Instance);

    private static BundledVideoTrack NonSimulcastTrack() =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            Outbound(), ReorderDepth, NullLoggerFactory.Instance);

    private static BundledOutboundPipeline Outbound() =>
        new(new RtpPacketCodec(), new DiscardSender(), NullLogger<BundledOutboundPipeline>.Instance);

    private static RtpPacket Packet(uint ssrc, ushort seq, bool marker, byte[] payload) => new()
    {
        Ssrc = ssrc,
        PayloadType = VideoPayloadType,
        SequenceNumber = seq,
        Timestamp = 90000u,
        Marker = marker,
        Payload = Vp8(payload),
    };

    // Minimal single-packet VP8 frame (RFC 7741 §4.2): 1-byte start-of-partition descriptor + body.
    private static byte[] Vp8(byte[] body)
    {
        var packet = new byte[1 + body.Length];
        packet[0] = 0x10;
        Array.Copy(body, 0, packet, 1, body.Length);
        return packet;
    }

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
