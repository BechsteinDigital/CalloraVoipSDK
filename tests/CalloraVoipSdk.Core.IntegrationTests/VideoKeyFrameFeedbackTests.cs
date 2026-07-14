using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video RTCP feedback (WebRTC phase 3, RFC 4585/5104): an inbound PLI/FIR surfaces a
/// keyframe request; detected loss reports the missing packets as a Generic NACK and a
/// throttled PLI — each gated on the peer's advertised feedback. Exercised against the
/// collaborator directly so the logic is deterministic (no sockets).
/// </summary>
public sealed class VideoKeyFrameFeedbackTests
{
    private const uint LocalSsrc = 0xABCDEF01;
    private static readonly RtcpPacketCodec Codec = new();

    // ── Inbound keyframe requests ─────────────────────────────────────────────────

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

        feedback.OnControlDatagram([0x81, 206]);

        Assert.Equal(0, requested);
    }

    // ── Loss → NACK / PLI, gated on advertised feedback ──────────────────────────

    [Fact]
    public void Loss_sends_a_nack_naming_the_missing_sequence_numbers()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsNack: true, supportsPli: false);
        const uint remoteSsrc = 0x22334455;

        // Missing 101, 102, 104 (gap around a delivered 103) → one entry PID=101, bits 0 and 2.
        feedback.OnLoss(remoteSsrc, [101, 102, 104]);

        var nack = Assert.IsType<RtcpGenericNack>(Assert.Single(Codec.Decode(Assert.Single(sent))));
        Assert.Equal(LocalSsrc, nack.SenderSsrc);
        Assert.Equal(remoteSsrc, nack.MediaSsrc);
        Assert.Equal((ushort[])[101, 102, 104], nack.LostSequenceNumbers().ToArray());
    }

    [Fact]
    public void Loss_without_advertised_nack_sends_no_nack()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsNack: false, supportsPli: false);

        feedback.OnLoss(0x1, [10, 11, 12]);

        Assert.Empty(sent);
    }

    [Fact]
    public void Loss_with_advertised_pli_sends_a_throttled_pli()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsNack: false, supportsPli: true);

        feedback.OnLoss(0x9, [5]);
        feedback.OnLoss(0x9, [6]); // within the 500 ms window — collapsed

        var pli = Assert.IsType<RtcpPictureLossIndication>(Assert.Single(Codec.Decode(Assert.Single(sent))));
        Assert.Equal(0x9u, pli.MediaSsrc);
    }

    [Fact]
    public void Loss_with_both_advertised_sends_nack_and_pli()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsNack: true, supportsPli: true);

        feedback.OnLoss(0x7, [200, 201]);

        var kinds = sent.SelectMany(d => Codec.Decode(d)).ToArray();
        Assert.Contains(kinds, p => p is RtcpGenericNack);
        Assert.Contains(kinds, p => p is RtcpPictureLossIndication);
    }

    [Fact]
    public void Nack_bitmask_spans_more_than_one_entry_for_a_wide_gap()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsNack: true, supportsPli: false);

        // 20 consecutive missing packets exceed one entry's 17-packet reach → two entries.
        var missing = Enumerable.Range(1000, 20).Select(i => (ushort)i).ToArray();
        feedback.OnLoss(0x1, missing);

        var nack = Assert.IsType<RtcpGenericNack>(Assert.Single(Codec.Decode(Assert.Single(sent))));
        Assert.True(nack.Entries.Count >= 2);
        Assert.Equal(missing, nack.LostSequenceNumbers().ToArray());
    }

    private static VideoKeyFrameFeedback CreateFeedback(
        Action onKeyFrameRequested, out List<byte[]> sentDatagrams,
        bool supportsNack = false, bool supportsPli = true)
    {
        var sent = new List<byte[]>();
        sentDatagrams = sent;
        return new VideoKeyFrameFeedback(
            Codec,
            LocalSsrc,
            supportsNack,
            supportsPli,
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
