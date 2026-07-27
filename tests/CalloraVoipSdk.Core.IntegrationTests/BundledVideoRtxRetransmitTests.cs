using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RTX retransmission on the bundled video track (ADR-011 B4, RFC 4585 + RFC 4588), mirroring the single-stream
/// <c>VideoRtxRetransmitE2eTests</c>: the track retains its sent packets, and on an inbound Generic NACK it
/// resends the requested packets on a separate RTX repair stream — own SSRC, the negotiated rtx payload type,
/// and the original sequence number carried as the OSN prefix (RFC 4588 §4). A NACK for a packet never sent
/// resends nothing; without an rtx payload type the inbound NACK is a no-op.
/// </summary>
public sealed class BundledVideoRtxRetransmitTests
{
    private const byte MidExtId = 3;
    private const byte VideoPayloadType = 96;
    private const byte RtxPayloadType = 98;
    // Retention filters on the video stream's send SSRC, so the outbound track and the video track share it.
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint RemoteSsrc = 0x0D0D0D0D;
    // A bundle-wide-distinct repair SSRC as the factory would allocate it (≠ audio/video primary).
    private const uint BundleRtxSsrc = 0x0C0C0C0C;

    private static readonly RtpPacketCodec RtpCodec = new();

    [Fact]
    public async Task An_inbound_nack_resends_the_requested_packet_as_rtx()
    {
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        using var track = VideoTrack(pipeline, remoteSupportsNack: true, rtxPayloadType: RtxPayloadType);

        // Send two frames; capture the first outbound video packet so we can compare the RTX-recovered payload.
        await track.SendFrameAsync(new byte[] { 0x10, 0xAA, 0xBB, 0xCC }, rtpTimestamp: 3000);
        await track.SendFrameAsync(new byte[] { 0x10, 0xDD, 0xEE, 0xFF }, rtpTimestamp: 6000);
        var videoPackets = sender.Captured.Select(d => RtpCodec.Decode(d)).Where(p => p.PayloadType == VideoPayloadType).ToArray();
        var first = videoPackets[0];

        // NACK the first video packet's sequence number.
        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpGenericNack
            {
                SenderSsrc = RemoteSsrc,
                MediaSsrc = VideoSsrc,
                Entries = new[] { new RtcpNackEntry { PacketId = first.SequenceNumber, LostPacketBitmask = 0 } },
            },
        });

        // The resend is fire-and-forget; drain it.
        var rtx = await WaitForRtxAsync(sender, expected: 1);
        var repair = Assert.Single(rtx);

        Assert.Equal(RtxPayloadType, repair.PayloadType);
        Assert.NotEqual(VideoSsrc, repair.Ssrc); // RTX uses its own repair SSRC (RFC 4588 §4)
        Assert.True(RtxPacketFactory.TryDecapsulate(repair, VideoPayloadType, VideoSsrc, out var recovered));
        Assert.Equal(first.SequenceNumber, recovered!.SequenceNumber); // OSN restores the original sequence
        Assert.Equal(VideoPayloadType, recovered.PayloadType);
        Assert.Equal(first.Payload.ToArray(), recovered.Payload.ToArray());
    }

    [Fact]
    public async Task The_repair_stream_carries_the_bundle_wide_rtx_ssrc_the_factory_allocated()
    {
        // The session factory owns bundle-wide SSRC allocation and hands the track a repair SSRC distinct from
        // every other outbound SSRC (RFC 4588 §4 / RFC 3550 §8.1). The track must send RTX on exactly that SSRC,
        // not one it picked itself — so the repair stream is the one the peer expects.
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        using var track = VideoTrack(pipeline, remoteSupportsNack: true, rtxPayloadType: RtxPayloadType, rtxSsrc: BundleRtxSsrc);

        await track.SendFrameAsync(new byte[] { 0x10, 0xAA, 0xBB, 0xCC }, rtpTimestamp: 3000);
        var first = sender.Captured.Select(d => RtpCodec.Decode(d)).First(p => p.PayloadType == VideoPayloadType);

        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpGenericNack
            {
                SenderSsrc = RemoteSsrc,
                MediaSsrc = VideoSsrc,
                Entries = new[] { new RtcpNackEntry { PacketId = first.SequenceNumber, LostPacketBitmask = 0 } },
            },
        });

        var repair = Assert.Single(await WaitForRtxAsync(sender, expected: 1));
        Assert.Equal(BundleRtxSsrc, repair.Ssrc); // the supplied repair SSRC, not a self-picked one
    }

    [Fact]
    public async Task Multiple_nacked_packets_resend_with_distinct_ascending_rtx_sequence_numbers()
    {
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        using var track = VideoTrack(pipeline, remoteSupportsNack: true, rtxPayloadType: RtxPayloadType);

        for (var i = 0; i < 4; i++)
            await track.SendFrameAsync(new byte[] { 0x10, 0x01, 0x02, 0x03 }, rtpTimestamp: (uint)((i + 1) * 3000));
        var videoPackets = sender.Captured.Select(d => RtpCodec.Decode(d)).Where(p => p.PayloadType == VideoPayloadType).ToArray();

        // NACK the first packet plus the two after it (bitmask bits 0 and 1) → three resends.
        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpGenericNack
            {
                SenderSsrc = RemoteSsrc,
                MediaSsrc = VideoSsrc,
                Entries = new[] { new RtcpNackEntry { PacketId = videoPackets[0].SequenceNumber, LostPacketBitmask = 0b11 } },
            },
        });

        var rtx = await WaitForRtxAsync(sender, expected: 3);
        var rtxSequences = rtx.Select(p => p.SequenceNumber).ToArray();
        Assert.Equal(3, rtxSequences.Distinct().Count());
        Assert.Equal(rtxSequences.OrderBy(s => s).ToArray(), rtxSequences); // the RTX stream numbers its own packets monotonically
        Assert.All(rtx, p => Assert.Equal(RtxPayloadType, p.PayloadType));
    }

    [Fact]
    public async Task A_nack_for_a_packet_never_sent_resends_nothing()
    {
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        using var track = VideoTrack(pipeline, remoteSupportsNack: true, rtxPayloadType: RtxPayloadType);

        await track.SendFrameAsync(new byte[] { 0x10, 0x01 }, rtpTimestamp: 3000);
        sender.Clear();

        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpGenericNack
            {
                SenderSsrc = RemoteSsrc,
                MediaSsrc = VideoSsrc,
                Entries = new[] { new RtcpNackEntry { PacketId = 40000, LostPacketBitmask = 0 } },
            },
        });

        await Task.Delay(200);
        Assert.Empty(sender.Captured.Select(d => RtpCodec.Decode(d)).Where(p => p.PayloadType == RtxPayloadType));
    }

    [Fact]
    public async Task Without_an_rtx_payload_type_an_inbound_nack_resends_nothing()
    {
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        // No rtx payload type: the retransmit callback stays a no-op (pre-RTX behaviour).
        using var track = VideoTrack(pipeline, remoteSupportsNack: true, rtxPayloadType: null);

        await track.SendFrameAsync(new byte[] { 0x10, 0x01, 0x02, 0x03 }, rtpTimestamp: 3000);
        var videoPackets = sender.Captured.Select(d => RtpCodec.Decode(d)).Where(p => p.PayloadType == VideoPayloadType).ToArray();
        sender.Clear();

        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpGenericNack
            {
                SenderSsrc = RemoteSsrc,
                MediaSsrc = VideoSsrc,
                Entries = new[] { new RtcpNackEntry { PacketId = videoPackets[0].SequenceNumber, LostPacketBitmask = 0 } },
            },
        });

        await Task.Delay(200);
        Assert.Empty(sender.Captured); // nothing resent at all
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BundledVideoTrack VideoTrack(
        BundledOutboundPipeline pipeline, bool remoteSupportsNack, byte? rtxPayloadType, uint? rtxSsrc = null) =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack, remoteSupportsPli: false,
            pipeline, reorderWindowDepth: 32, NullLoggerFactory.Instance, rtxPayloadType, rtxSsrc);

    // A minimal outbound pipeline over the capturing sender with identity SRTP/SRTCP contexts installed so the
    // RTP sends (and the RTX resend) go through the fail-closed protect path and the captured datagram is the
    // plaintext packet. The video outbound track shares VideoSsrc so retention keys on this stream's SSRC.
    private static BundledOutboundPipeline Outbound(CapturingSender sender)
    {
        var pipeline = new BundledOutboundPipeline(new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video", new BundledOutboundTrack(
            VideoSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "video"),
            initialSequenceNumber: 1000, initialTimestamp: 90000));
        pipeline.InstallOutboundKey(new IdentitySrtpContext());
        pipeline.InstallOutboundRtcpKey(new IdentitySrtcpContext());
        return pipeline;
    }

    // The resend is fire-and-forget on the RTCP-handling thread; poll the captured datagrams until the expected
    // number of RTX packets appears (or time out so a regression fails loudly rather than hanging).
    private static async Task<IReadOnlyList<RtpPacket>> WaitForRtxAsync(CapturingSender sender, int expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var rtx = sender.Captured.Select(d => RtpCodec.Decode(d)).Where(p => p.PayloadType == RtxPayloadType).ToArray();
            if (rtx.Length >= expected)
                return rtx;
            await Task.Delay(10);
        }

        throw new TimeoutException($"Expected {expected} RTX packet(s) but the sender captured fewer.");
    }

    // Records every datagram the pipeline sends so the test can decode and inspect the outbound RTP/RTX. The
    // RTX resend is fire-and-forget on a thread-pool thread, so every access is synchronised.
    private sealed class CapturingSender : IBundledDatagramSender
    {
        private readonly List<byte[]> _captured = new();

        /// <summary>A point-in-time snapshot of the captured datagrams, safe to read while sends may be in flight.</summary>
        public IReadOnlyList<byte[]> Captured
        {
            get { lock (_captured) return _captured.ToArray(); }
        }

        public void Clear()
        {
            lock (_captured) _captured.Clear();
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            lock (_captured)
                _captured.Add(datagram.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    // Identity SRTP: leaves the plaintext RTP packet untouched, so the captured datagram decodes directly.
    private sealed class IdentitySrtpContext : ISrtpContext
    {
        public byte[] Protect(ReadOnlySpan<byte> rtpPacket) => rtpPacket.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> srtpPacket) => srtpPacket.ToArray();
        public void Dispose() { }
    }

    // Identity SRTCP: leaves the plaintext RTCP compound untouched (RTX itself never uses this path).
    private sealed class IdentitySrtcpContext : ISrtcpContext
    {
        public byte[] ProtectRtcp(ReadOnlySpan<byte> rtcpPacket) => rtcpPacket.ToArray();
        public byte[] UnprotectRtcp(ReadOnlySpan<byte> srtcpPacket) => srtcpPacket.ToArray();
        public void Dispose() { }
    }
}
