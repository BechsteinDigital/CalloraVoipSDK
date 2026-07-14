using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video keyframe feedback (WebRTC phase 3 slice 2, RFC 4585/5104): an inbound PLI/FIR
/// surfaces a keyframe request; a detected loss sends a throttled PLI to the peer.
/// Exercised directly against the collaborator so the logic is deterministic (no sockets).
/// </summary>
public sealed class VideoKeyFrameFeedbackTests
{
    private const uint LocalSsrc = 0xABCDEF01;
    private static readonly RtcpPacketCodec Codec = new();

    [Fact]
    public void Inbound_pli_raises_a_keyframe_request()
    {
        var requested = 0;
        var feedback = CreateFeedback(onKeyFrameRequested: () => requested++, out _);

        feedback.OnControlDatagram(Codec.Encode(
            [new RtcpPictureLossIndication { SenderSsrc = 1, MediaSsrc = LocalSsrc }]));

        Assert.Equal(1, requested);
    }

    [Fact]
    public void Inbound_fir_raises_a_keyframe_request()
    {
        var requested = 0;
        var feedback = CreateFeedback(onKeyFrameRequested: () => requested++, out _);

        feedback.OnControlDatagram(Codec.Encode([new RtcpFullIntraRequest
        {
            SenderSsrc = 1,
            Entries = [new RtcpFirEntry { MediaSsrc = LocalSsrc, SequenceNumber = 3 }],
        }]));

        Assert.Equal(1, requested);
    }

    [Fact]
    public void Inbound_receiver_report_does_not_raise_a_keyframe_request()
    {
        var requested = 0;
        var feedback = CreateFeedback(onKeyFrameRequested: () => requested++, out _);

        feedback.OnControlDatagram(Codec.Encode(
            [new RtcpReceiverReport { Ssrc = LocalSsrc, ReportBlocks = [] }]));

        Assert.Equal(0, requested);
    }

    [Fact]
    public void Malformed_inbound_datagram_is_dropped_without_throwing()
    {
        var requested = 0;
        var feedback = CreateFeedback(onKeyFrameRequested: () => requested++, out _);

        // Truncated RTCP (2 bytes) — must neither throw nor raise a request.
        feedback.OnControlDatagram([0x81, 206]);

        Assert.Equal(0, requested);
    }

    [Fact]
    public void Detected_loss_sends_one_pli_targeting_the_remote_source()
    {
        var feedback = CreateFeedback(onKeyFrameRequested: () => { }, out var sent);
        const uint remoteSsrc = 0x22334455;

        feedback.RequestRemoteKeyFrame(remoteSsrc);

        var datagram = Assert.Single(sent);
        var pli = Assert.IsType<RtcpPictureLossIndication>(Assert.Single(Codec.Decode(datagram)));
        Assert.Equal(LocalSsrc, pli.SenderSsrc);
        Assert.Equal(remoteSsrc, pli.MediaSsrc);
    }

    [Fact]
    public void Rapid_loss_is_throttled_to_a_single_pli()
    {
        var feedback = CreateFeedback(onKeyFrameRequested: () => { }, out var sent);

        // Three losses within the 500 ms window collapse to one PLI (baresip-style throttle).
        feedback.RequestRemoteKeyFrame(0x1);
        feedback.RequestRemoteKeyFrame(0x1);
        feedback.RequestRemoteKeyFrame(0x1);

        Assert.Single(sent);
    }

    private static VideoKeyFrameFeedback CreateFeedback(
        Action onKeyFrameRequested, out List<byte[]> sentDatagrams)
    {
        var sent = new List<byte[]>();
        sentDatagrams = sent;
        return new VideoKeyFrameFeedback(
            Codec,
            LocalSsrc,
            (datagram, _) =>
            {
                sent.Add(datagram.ToArray());
                return ValueTask.CompletedTask;
            },
            onKeyFrameRequested,
            NullLogger.Instance,
            CancellationToken.None);
    }
}
