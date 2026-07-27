using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Inbound RTCP feedback on the bundled video track (ADR-011 B4, RFC 4585/5104), mirroring the single-stream
/// <c>VideoKeyFrameFeedback</c>:
/// <list type="bullet">
/// <item>a PLI or FIR anywhere in the decoded compound is a request to send a key frame, surfaced on
/// <c>KeyFrameRequested</c> so the app can encode one (a report-only SR/RR compound raises nothing);</item>
/// <item>a genuine forward gap in the inbound RTP sequence is reported to the peer as an outbound Generic NACK
/// (RFC 4585 §6.2.1) naming the missing sequence numbers — gated on the peer advertising <c>a=rtcp-fb nack</c>;
/// an in-order or reordered arrival reports nothing.</item>
/// </list>
/// RTX/retransmit-on-inbound-NACK is out of scope for this slice.
/// </summary>
public sealed class BundledVideoKeyFrameRequestTests
{
    private const byte MidExtId = 3;
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint LocalSsrc = 0x0A0A0A0A;
    private const uint RemoteSsrc = 0x0D0D0D0D;

    [Fact]
    public void An_inbound_pli_raises_a_key_frame_request()
    {
        using var track = VideoTrack(new CapturingSender());
        var raised = 0;
        track.KeyFrameRequested += () => raised++;

        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpPictureLossIndication { SenderSsrc = LocalSsrc, MediaSsrc = VideoSsrc },
        });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void An_inbound_fir_raises_a_key_frame_request()
    {
        using var track = VideoTrack(new CapturingSender());
        var raised = 0;
        track.KeyFrameRequested += () => raised++;

        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpFullIntraRequest
            {
                SenderSsrc = LocalSsrc,
                Entries = new[] { new RtcpFirEntry { MediaSsrc = VideoSsrc, SequenceNumber = (byte)1 } },
            },
        });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_report_only_compound_does_not_raise_a_key_frame_request()
    {
        using var track = VideoTrack(new CapturingSender());
        var raised = 0;
        track.KeyFrameRequested += () => raised++;

        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpReceiverReport { Ssrc = LocalSsrc },
        });

        Assert.Equal(0, raised);
    }

    [Fact]
    public void An_inbound_sequence_gap_sends_a_generic_nack_naming_the_missing_packets()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(sender, remoteSupportsNack: true);

        // 100 establishes the reference; jumping to 104 is a forward gap of 101, 102, 103. The NACK is
        // deferred and reorder-tolerant (libwebrtc/Pion): each missing sequence is only NACKed once the stream
        // has advanced past it beyond the reorder window, so keep advancing until the whole gap has aged out.
        track.OnRtpPacket(VideoPacket(sequence: 100));
        foreach (var seq in new ushort[] { 104, 105, 106, 107 })
            track.OnRtpPacket(VideoPacket(seq));

        var nacks = sender.Captured.SelectMany(Decode).OfType<RtcpGenericNack>().ToArray();
        Assert.NotEmpty(nacks);
        Assert.All(nacks, n =>
        {
            Assert.Equal(LocalSsrc, n.SenderSsrc);   // our outbound SSRC is the feedback sender
            Assert.Equal(RemoteSsrc, n.MediaSsrc);   // the media source we lost packets from
        });
        // Across the aged-out reports, exactly the three missing sequences are NACKed — no more, no fewer.
        var nacked = nacks.SelectMany(n => n.LostSequenceNumbers()).Distinct().OrderBy(s => s).ToArray();
        Assert.Equal(new ushort[] { 101, 102, 103 }, nacked);
    }

    [Fact]
    public void An_in_order_inbound_sequence_sends_no_nack()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(sender, remoteSupportsNack: true);

        track.OnRtpPacket(VideoPacket(sequence: 100));
        track.OnRtpPacket(VideoPacket(sequence: 101));
        track.OnRtpPacket(VideoPacket(sequence: 102));

        Assert.Empty(sender.Captured.SelectMany(Decode).OfType<RtcpGenericNack>());
    }

    [Fact]
    public void A_gap_sends_no_nack_when_the_peer_did_not_advertise_nack()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(sender, remoteSupportsNack: false);

        track.OnRtpPacket(VideoPacket(sequence: 100));
        track.OnRtpPacket(VideoPacket(sequence: 104));

        Assert.Empty(sender.Captured.SelectMany(Decode).OfType<RtcpGenericNack>());
    }

    [Fact]
    public void A_gap_sends_a_pli_when_the_peer_advertised_pli()
    {
        var sender = new CapturingSender();
        using var track = VideoTrack(sender, remoteSupportsPli: true);

        track.OnRtpPacket(VideoPacket(sequence: 100));
        track.OnRtpPacket(VideoPacket(sequence: 104));

        var pli = Assert.Single(sender.Captured.SelectMany(Decode).OfType<RtcpPictureLossIndication>());
        Assert.Equal(LocalSsrc, pli.SenderSsrc);
        Assert.Equal(RemoteSsrc, pli.MediaSsrc);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BundledVideoTrack VideoTrack(
        CapturingSender sender, bool remoteSupportsNack = false, bool remoteSupportsPli = false) =>
        new("video", "H264", VideoPayloadType, LocalSsrc, remoteSupportsNack, remoteSupportsPli,
            Outbound(sender), reorderWindowDepth: 32, NullLoggerFactory.Instance);

    private static RtpPacket VideoPacket(ushort sequence) => new()
    {
        PayloadType = VideoPayloadType,
        SequenceNumber = sequence,
        Timestamp = 90000,
        Ssrc = RemoteSsrc,
        Payload = new byte[] { 0x00 },
    };

    // A minimal outbound pipeline over the capturing sender, with an identity SRTCP context installed so the
    // feedback RTCP is sent (fail-closed otherwise) and the captured datagram is the plaintext compound.
    private static BundledOutboundPipeline Outbound(CapturingSender sender)
    {
        var pipeline = new BundledOutboundPipeline(new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video", new BundledOutboundTrack(
            VideoSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "video"),
            initialSequenceNumber: 1000, initialTimestamp: 90000));
        pipeline.InstallOutboundRtcpKey(new IdentitySrtcpContext());
        return pipeline;
    }

    private static IReadOnlyList<RtcpPacket> Decode(byte[] datagram) => new RtcpPacketCodec().Decode(datagram);

    // Records every datagram the pipeline sends so the test can decode and inspect the outbound RTCP.
    private sealed class CapturingSender : IBundledDatagramSender
    {
        public List<byte[]> Captured { get; } = new();

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            Captured.Add(datagram.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    // Identity SRTCP: leaves the plaintext RTCP compound untouched, so the captured datagram decodes directly.
    private sealed class IdentitySrtcpContext : ISrtcpContext
    {
        public byte[] ProtectRtcp(ReadOnlySpan<byte> rtcpPacket) => rtcpPacket.ToArray();
        public byte[] UnprotectRtcp(ReadOnlySpan<byte> srtcpPacket) => srtcpPacket.ToArray();
        public void Dispose() { }
    }
}
