using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Inbound RTCP feedback on the bundled video track (ADR-011 B4, RFC 4585/5104): a PLI or FIR anywhere in
/// the decoded compound is a request to send a key frame, surfaced on <c>KeyFrameRequested</c> so the app
/// can encode one. A report-only compound (SR/RR) raises nothing. NACK/RTX are out of scope for this slice.
/// </summary>
public sealed class BundledVideoKeyFrameRequestTests
{
    private const byte MidExtId = 3;
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint LocalSsrc = 0x0A0A0A0A;

    [Fact]
    public void An_inbound_pli_raises_a_key_frame_request()
    {
        using var track = VideoTrack();
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
        using var track = VideoTrack();
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
        using var track = VideoTrack();
        var raised = 0;
        track.KeyFrameRequested += () => raised++;

        track.OnRtcpPackets(new RtcpPacket[]
        {
            new RtcpReceiverReport { Ssrc = LocalSsrc },
        });

        Assert.Equal(0, raised);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BundledVideoTrack VideoTrack() =>
        new("video", "H264", VideoPayloadType, Outbound(), reorderWindowDepth: 32, NullLogger<BundledVideoTrack>.Instance);

    // A minimal outbound pipeline: the track needs one to construct, but the key-frame-request path never sends.
    private static BundledOutboundPipeline Outbound()
    {
        var pipeline = new BundledOutboundPipeline(new RtpPacketCodec(), new DiscardSender(), NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video", new BundledOutboundTrack(
            VideoSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "video"),
            initialSequenceNumber: 1000, initialTimestamp: 90000));
        return pipeline;
    }

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
