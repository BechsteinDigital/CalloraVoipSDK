using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Inbound RTCP feedback routing on a BUNDLE (#161 P2-5). One RTCP channel carries the feedback for every
/// m-line, and the dispatcher fans the whole decoded compound to every video track — so each track has to
/// keep only what names one of its own sending SSRCs (RFC 4585 §6.2.1 / RFC 5104 §4.3.1). Otherwise a PLI
/// for one track asks every track for a key frame, and a NACK for one is looked up in another track's
/// retransmission buffer, whose 16-bit sequence space overlaps — resending unrelated packets as RTX.
/// </summary>
public sealed class BundledVideoFeedbackRoutingTests
{
    private const byte MidExtId = 3;
    private const byte VideoPayloadType = 96;
    private const byte RtxPayloadType = 98;
    private const uint FirstSsrc = 0x0A0A0A0A;
    private const uint SecondSsrc = 0x0B0B0B0B;
    private const uint FirstRtxSsrc = 0x0A0A0A0B;
    private const uint SecondRtxSsrc = 0x0B0B0B0C;
    private const uint RemoteSsrc = 0x0D0D0D0D;

    private static readonly RtpPacketCodec RtpCodec = new();

    // Both m-lines start their sequence space at the same number, which is exactly the overlap the finding
    // describes: a NACK for one track names sequence numbers the other track has sent as well.
    private static BundledOutboundPipeline Outbound(CapturingSender sender)
    {
        var pipeline = new BundledOutboundPipeline(new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video1", new BundledOutboundTrack(
            FirstSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "video1"),
            initialSequenceNumber: 1000, initialTimestamp: 90000));
        pipeline.RegisterTrack("video2", new BundledOutboundTrack(
            SecondSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "video2"),
            initialSequenceNumber: 1000, initialTimestamp: 90000));
        pipeline.InstallOutboundKey(new IdentitySrtpContext());
        pipeline.InstallOutboundRtcpKey(new IdentitySrtcpContext());
        return pipeline;
    }

    private static BundledVideoTrack VideoTrack(BundledOutboundPipeline pipeline, string mid, uint ssrc, uint rtxSsrc) =>
        new(mid, "VP8", VideoPayloadType, ssrc, remoteSupportsNack: true, remoteSupportsPli: true,
            pipeline, reorderWindowDepth: 32, NullLoggerFactory.Instance, RtxPayloadType, rtxSsrc);

    private static async Task<IReadOnlyList<RtpPacket>> DrainRtxAsync(CapturingSender sender)
    {
        // The resend is fire-and-forget; give it a moment, then take everything that went out.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (sender.Captured.Count > 0)
                break;
            await Task.Delay(10);
        }
        await Task.Delay(50); // a second, wrongly-routed resend would land in this window

        return sender.Captured.Select(d => RtpCodec.Decode(d)).Where(p => p.PayloadType == RtxPayloadType).ToArray();
    }

    [Fact]
    public void A_pli_only_reaches_the_track_whose_ssrc_it_names()
    {
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        using var first = VideoTrack(pipeline, "video1", FirstSsrc, FirstRtxSsrc);
        using var second = VideoTrack(pipeline, "video2", SecondSsrc, SecondRtxSsrc);

        var firstRequests = 0;
        var secondRequests = 0;
        first.KeyFrameRequested += () => firstRequests++;
        second.KeyFrameRequested += () => secondRequests++;

        RtcpPacket[] compound = [new RtcpPictureLossIndication { SenderSsrc = RemoteSsrc, MediaSsrc = SecondSsrc }];
        first.OnRtcpPackets(compound);
        second.OnRtcpPackets(compound);

        Assert.Equal(0, firstRequests);
        Assert.Equal(1, secondRequests);
    }

    [Fact]
    public void A_fir_only_reaches_the_track_its_entries_name()
    {
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        using var first = VideoTrack(pipeline, "video1", FirstSsrc, FirstRtxSsrc);
        using var second = VideoTrack(pipeline, "video2", SecondSsrc, SecondRtxSsrc);

        var firstRequests = 0;
        var secondRequests = 0;
        first.KeyFrameRequested += () => firstRequests++;
        second.KeyFrameRequested += () => secondRequests++;

        // FIR names its targets in the FCI entries, not in a header field (RFC 5104 §4.3.1).
        RtcpPacket[] compound =
        [
            new RtcpFullIntraRequest
            {
                SenderSsrc = RemoteSsrc,
                Entries = [new RtcpFirEntry { MediaSsrc = FirstSsrc, SequenceNumber = 7 }],
            },
        ];
        first.OnRtcpPackets(compound);
        second.OnRtcpPackets(compound);

        Assert.Equal(1, firstRequests);
        Assert.Equal(0, secondRequests);
    }

    [Fact]
    public void Feedback_naming_an_unrelated_source_reaches_no_track()
    {
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        using var first = VideoTrack(pipeline, "video1", FirstSsrc, FirstRtxSsrc);
        using var second = VideoTrack(pipeline, "video2", SecondSsrc, SecondRtxSsrc);

        var requests = 0;
        first.KeyFrameRequested += () => requests++;
        second.KeyFrameRequested += () => requests++;

        // A lenient peer's 0, and a stream neither track sends: on a shared channel neither can be attributed.
        RtcpPacket[] compound =
        [
            new RtcpPictureLossIndication { SenderSsrc = RemoteSsrc, MediaSsrc = 0 },
            new RtcpPictureLossIndication { SenderSsrc = RemoteSsrc, MediaSsrc = 0x00C0FFEE },
        ];
        first.OnRtcpPackets(compound);
        second.OnRtcpPackets(compound);

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task A_nack_never_retransmits_the_other_tracks_packet_with_the_same_sequence()
    {
        var sender = new CapturingSender();
        var pipeline = Outbound(sender);
        using var first = VideoTrack(pipeline, "video1", FirstSsrc, FirstRtxSsrc);
        using var second = VideoTrack(pipeline, "video2", SecondSsrc, SecondRtxSsrc);

        await first.SendFrameAsync(new byte[] { 0x10, 0xAA, 0xBB, 0xCC }, rtpTimestamp: 3000);
        await second.SendFrameAsync(new byte[] { 0x10, 0xDD, 0xEE, 0xFF }, rtpTimestamp: 3000);

        var sent = sender.Captured.Select(d => RtpCodec.Decode(d)).Where(p => p.PayloadType == VideoPayloadType).ToArray();
        var fromFirst = Assert.Single(sent.Where(p => p.Ssrc == FirstSsrc));
        var fromSecond = Assert.Single(sent.Where(p => p.Ssrc == SecondSsrc));
        Assert.Equal(fromFirst.SequenceNumber, fromSecond.SequenceNumber); // the overlapping sequence space
        sender.Clear();

        RtcpPacket[] compound =
        [
            new RtcpGenericNack
            {
                SenderSsrc = RemoteSsrc,
                MediaSsrc = FirstSsrc,
                Entries = [new RtcpNackEntry { PacketId = fromFirst.SequenceNumber, LostPacketBitmask = 0 }],
            },
        ];
        first.OnRtcpPackets(compound);
        second.OnRtcpPackets(compound);

        var rtx = await DrainRtxAsync(sender);
        var repair = Assert.Single(rtx);
        Assert.Equal(FirstRtxSsrc, repair.Ssrc); // only the named track answered
    }

    private sealed class CapturingSender : IBundledDatagramSender
    {
        private readonly List<byte[]> _captured = new();

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

    // Identity SRTP/SRTCP: leave the plaintext untouched, so a captured datagram decodes directly.
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
