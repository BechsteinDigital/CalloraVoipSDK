using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The bundled video track's getStats counters (WebRTC getStats video fields): frames dropped when a
/// reorder gap tears frame assembly, and the NACK/PLI feedback messages the track has sent to the peer on
/// detected inbound loss (surfaced from the shared <see cref="VideoKeyFrameFeedback"/>). Driven against the
/// track's router sink directly so the counters are deterministic (no sockets).
/// </summary>
public sealed class BundledVideoTrackStatsTests
{
    private const byte VideoPayloadType = 96;
    private const uint LocalSsrc = 0x0B0B0B0B;
    private const uint RemoteMediaSsrc = 0x0A0B0C0D;

    private static readonly Vp8Packetiser Packetiser = new();

    [Fact]
    public void A_reorder_gap_the_window_cannot_fill_counts_a_dropped_frame()
    {
        using var track = VideoTrack(Outbound(new CapturingSender()), remoteSupportsNack: false, remoteSupportsPli: false);

        // Frame 1 is delivered; sequence 2 is then permanently missing. Feeding well past the reorder window
        // (depth 32) forces the buffer to give up on 2 and release 3 out of order → the depacketiser resets on
        // the discontinuity, tearing the frame under assembly → one dropped frame.
        track.OnRtpPacket(Primary(1));
        for (ushort seq = 3; seq <= 48; seq++)
            track.OnRtpPacket(Primary(seq));

        Assert.True(track.FramesDropped >= 1, $"expected a dropped frame after an unfillable gap, got {track.FramesDropped}");
    }

    [Fact]
    public void A_contiguous_stream_drops_no_frames()
    {
        using var track = VideoTrack(Outbound(new CapturingSender()), remoteSupportsNack: false, remoteSupportsPli: false);

        for (ushort seq = 1; seq <= 30; seq++)
            track.OnRtpPacket(Primary(seq));

        Assert.Equal(0, track.FramesDropped);
    }

    [Fact]
    public void Detected_inbound_loss_counts_the_sent_nack_and_pli()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(Outbound(sender), remoteSupportsNack: true, remoteSupportsPli: true);

        // Sequence 2 is missing; the arrival-order tracker holds it until the stream advances past the reorder
        // tolerance, then reports it → the track sends a NACK naming it plus a PLI. The counters mirror the sends.
        track.OnRtpPacket(Primary(1));
        for (ushort seq = 3; seq <= 12; seq++)
            track.OnRtpPacket(Primary(seq));

        Assert.True(track.NacksSent >= 1, $"expected a sent NACK, got {track.NacksSent}");
        Assert.True(track.PlisSent >= 1, $"expected a sent PLI, got {track.PlisSent}");

        // The counters agree with the RTCP actually captured on the wire.
        var codec = new RtcpPacketCodec();
        var feedback = sender.Captured.SelectMany(d => codec.Decode(d)).ToArray();
        Assert.Equal(track.NacksSent, feedback.Count(p => p is Application.Media.Rtcp.Packets.RtcpGenericNack));
        Assert.Equal(track.PlisSent, feedback.Count(p => p is Application.Media.Rtcp.Packets.RtcpPictureLossIndication));
    }

    [Fact]
    public void No_loss_sends_no_feedback_and_keeps_counters_zero()
    {
        using var track = VideoTrack(Outbound(new CapturingSender()), remoteSupportsNack: true, remoteSupportsPli: true);

        for (ushort seq = 1; seq <= 20; seq++)
            track.OnRtpPacket(Primary(seq));

        Assert.Equal(0, track.NacksSent);
        Assert.Equal(0, track.PlisSent);
    }

    [Fact]
    public async Task App_keyframe_request_sends_a_pli_naming_the_received_stream()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(Outbound(sender), remoteSupportsNack: false, remoteSupportsPli: true);

        // A primary arrival captures the remote media SSRC that an app-driven PLI must name; a single contiguous
        // packet triggers no loss feedback, so the only PLI on the wire is the app-requested one.
        track.OnRtpPacket(Primary(1));
        var sentPli = await track.RequestKeyFrameAsync();

        Assert.True(sentPli);
        Assert.Equal(1, track.PlisSent);
        var codec = new RtcpPacketCodec();
        var pli = Assert.IsType<Application.Media.Rtcp.Packets.RtcpPictureLossIndication>(
            Assert.Single(sender.Captured.SelectMany(d => codec.Decode(d))));
        Assert.Equal(LocalSsrc, pli.SenderSsrc);
        Assert.Equal(RemoteMediaSsrc, pli.MediaSsrc);
    }

    [Fact]
    public async Task App_keyframe_request_without_advertised_pli_is_a_no_op()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(Outbound(sender), remoteSupportsNack: false, remoteSupportsPli: false);
        track.OnRtpPacket(Primary(1));

        var sentPli = await track.RequestKeyFrameAsync();

        Assert.False(sentPli);
        Assert.Equal(0, track.PlisSent);
        Assert.Empty(sender.Captured);
    }

    // ── harness (mirrors BundledVideoRtxReceiveTests) ─────────────────────────────

    private static BundledVideoTrack VideoTrack(
        BundledOutboundPipeline pipeline, bool remoteSupportsNack, bool remoteSupportsPli) =>
        new("video", "VP8", VideoPayloadType, LocalSsrc, remoteSupportsNack, remoteSupportsPli,
            pipeline, reorderWindowDepth: 32, NullLoggerFactory.Instance);

    private static RtpPacket Primary(ushort seq)
    {
        var payloads = Packetiser.Packetise(MakeFrame(seq), 1200);
        return new RtpPacket
        {
            PayloadType = VideoPayloadType,
            SequenceNumber = seq,
            Timestamp = seq * 3000u,
            Marker = payloads[0].IsLastOfFrame,
            Ssrc = RemoteMediaSsrc,
            Payload = payloads[0].Payload,
        };
    }

    private static byte[] MakeFrame(ushort id)
    {
        var frame = new byte[40];
        frame[0] = (byte)id;
        for (var i = 1; i < frame.Length; i++)
            frame[i] = (byte)(i * 5 + id);
        return frame;
    }

    private static BundledOutboundPipeline Outbound(CapturingSender sender)
    {
        var pipeline = new BundledOutboundPipeline(new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video", new BundledOutboundTrack(
            LocalSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, midExtensionId: 3, "video"),
            initialSequenceNumber: 1000, initialTimestamp: 90000));
        pipeline.InstallOutboundKey(new IdentitySrtpContext());
        pipeline.InstallOutboundRtcpKey(new IdentitySrtcpContext());
        return pipeline;
    }

    private sealed class CapturingSender : IBundledDatagramSender
    {
        private readonly List<byte[]> _captured = new();

        public IReadOnlyList<byte[]> Captured
        {
            get { lock (_captured) return _captured.ToArray(); }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            lock (_captured)
                _captured.Add(datagram.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IdentitySrtpContext : ISrtpContext
    {
        public byte[] Protect(ReadOnlySpan<byte> rtpPacket) => rtpPacket.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> srtpPacket) => srtpPacket.ToArray();
        public void Dispose() { }
    }

    private sealed class IdentitySrtcpContext : ISrtcpContext
    {
        public byte[] ProtectRtcp(ReadOnlySpan<byte> rtcpPacket) => rtcpPacket.ToArray();
        public byte[] UnprotectRtcp(ReadOnlySpan<byte> srtcpPacket) => srtcpPacket.ToArray();
        public void Dispose() { }
    }
}
