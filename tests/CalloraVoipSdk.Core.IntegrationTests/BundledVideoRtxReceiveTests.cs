using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RTX receive on the bundled video track (ADR-011 B4, RFC 4585 + RFC 4588), mirroring the single-stream
/// <c>VideoRtxReceiveE2eTests</c>: the peer's repair stream shares the video MID, so the router sink
/// (<see cref="BundledVideoTrack.OnRtpPacket"/>) receives it alongside the primary stream. A packet on the
/// rtx payload type is decapsulated (RFC 4588 §4) and the recovered original is fed into the same reorder
/// window — filling the gap of a dropped packet so the recovered frame is depacketised and surfaced in
/// sequence order. An RTX for a sequence that was never missing is harmlessly absorbed, and a recovered
/// packet never triggers a new NACK.
/// </summary>
public sealed class BundledVideoRtxReceiveTests
{
    private const byte VideoPayloadType = 96;
    private const byte RtxPayloadType = 98;
    private const uint LocalSsrc = 0x0B0B0B0B;
    private const uint RemoteMediaSsrc = 0x0A0B0C0D;
    private const uint RemoteRtxSsrc = 0x0BADF00D;

    private static readonly Vp8Packetiser Packetiser = new();

    [Fact]
    public void Dropped_packet_recovered_via_rtx_is_delivered_in_order()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(Outbound(sender), remoteSupportsNack: true, rtxPayloadType: RtxPayloadType);

        var delivered = new List<int>();
        track.FrameReceived += (frame, _, _, _) => delivered.Add(frame[0]);

        // Frames 1 and 2 arrive; 3 is dropped but retransmitted as RTX; the stream keeps flowing so the
        // reorder window releases in order once 3 slots into its gap.
        track.OnRtpPacket(Primary(1));
        track.OnRtpPacket(Primary(2));
        track.OnRtpPacket(Rtx(originalSeq: 3, rtxSeq: 1));
        for (ushort seq = 4; seq <= 64; seq++)
            track.OnRtpPacket(Primary(seq));

        Assert.Contains(2, delivered);
        Assert.Contains(3, delivered); // the recovered frame appears
        Assert.Contains(4, delivered);
        Assert.Equal(delivered.OrderBy(id => id).ToArray(), delivered.ToArray()); // in ascending order
    }

    [Fact]
    public void Rtx_for_a_sequence_that_was_never_missing_is_harmless()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(Outbound(sender), remoteSupportsNack: true, rtxPayloadType: RtxPayloadType);

        var delivered = new List<int>();
        track.FrameReceived += (frame, _, _, _) => delivered.Add(frame[0]);

        // Every primary packet arrives in order; then a stale RTX for an already-released sequence arrives.
        for (ushort seq = 1; seq <= 10; seq++)
            track.OnRtpPacket(Primary(seq));
        track.OnRtpPacket(Rtx(originalSeq: 5, rtxSeq: 1)); // 5 was never missing — already delivered

        // The duplicate is absorbed by the reorder window (dropped as too-late): no frame is delivered twice.
        Assert.Equal(Enumerable.Range(1, 10), delivered);
    }

    [Fact]
    public void A_recovered_rtx_packet_does_not_trigger_a_new_nack()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(Outbound(sender), remoteSupportsNack: true, rtxPayloadType: RtxPayloadType);

        // Every primary packet arrives in order, so the arrival-order loss tracker never sees a forward gap and
        // no NACK is due from the primary path. Interleave an RTX carrying a far-lower rtx sequence number (1):
        // if the repair packet were (wrongly) run through arrival-order loss detection, that backward jump would
        // be read as a huge forward gap and provoke a NACK. Feeding it only into the reorder window must not.
        for (ushort seq = 1; seq <= 5; seq++)
        {
            track.OnRtpPacket(Primary(seq));
            track.OnRtpPacket(Rtx(originalSeq: seq, rtxSeq: seq)); // duplicate repair, absorbed by the reorder window
        }

        // No loss feedback was ever emitted. The track sends RTCP only on a detected gap (NACK/PLI); an empty
        // outbound capture proves the interleaved RTX packets were never run through arrival-order loss detection.
        var codec = new RtcpPacketCodec();
        var feedback = sender.Captured.SelectMany(d => codec.Decode(d)).ToArray();
        Assert.Empty(feedback);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BundledVideoTrack VideoTrack(
        BundledOutboundPipeline pipeline, bool remoteSupportsNack, byte? rtxPayloadType) =>
        new("video", "VP8", VideoPayloadType, LocalSsrc, remoteSupportsNack, remoteSupportsPli: false,
            pipeline, reorderWindowDepth: 32, NullLoggerFactory.Instance, rtxPayloadType);

    // A primary inbound video packet carrying frame id <paramref name="seq"/> (round-trips through the VP8
    // packetiser/depacketiser). Timestamp advances per frame so each is a distinct frame.
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

    // A repair packet for the original with sequence <paramref name="originalSeq"/> (RFC 4588 §4): rtx payload
    // type and SSRC, own rtx sequence number, OSN prefix. This is what the router sink hands OnRtpPacket after
    // demuxing the peer's repair stream to the shared video MID.
    private static RtpPacket Rtx(ushort originalSeq, ushort rtxSeq) =>
        RtxPacketFactory.Encapsulate(Primary(originalSeq), RtxPayloadType, RemoteRtxSsrc, rtxSeq);

    // A small VP8 frame that fits one RTP packet; frame[0] carries the id so delivery can be identified.
    private static byte[] MakeFrame(ushort id)
    {
        var frame = new byte[40];
        frame[0] = (byte)id;
        for (var i = 1; i < frame.Length; i++)
            frame[i] = (byte)(i * 5 + id);
        return frame;
    }

    // A minimal outbound pipeline over a capturing sender with identity SRTP/SRTCP contexts, so any outbound
    // RTCP (e.g. a NACK the track wrongly emitted) is captured as plaintext for inspection.
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
