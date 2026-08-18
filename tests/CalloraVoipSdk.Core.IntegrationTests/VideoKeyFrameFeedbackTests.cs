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

        feedback.OnRtcpPackets(
            [new RtcpPictureLossIndication { SenderSsrc = 1, MediaSsrc = LocalSsrc }]);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void Inbound_fir_raises_a_keyframe_request()
    {
        var requested = 0;
        var feedback = CreateFeedback(onKeyFrameRequested: () => requested++, out _);

        feedback.OnRtcpPackets([new RtcpFullIntraRequest
        {
            SenderSsrc = 1,
            Entries = [new RtcpFirEntry { MediaSsrc = LocalSsrc, SequenceNumber = 3 }],
        }]);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void Inbound_receiver_report_does_not_raise_a_keyframe_request()
    {
        var requested = 0;
        var feedback = CreateFeedback(onKeyFrameRequested: () => requested++, out _);

        feedback.OnRtcpPackets(
            [new RtcpReceiverReport { Ssrc = LocalSsrc, ReportBlocks = [] }]);

        Assert.Equal(0, requested);
    }

    // (The malformed-datagram drop moved to RtpSession, which decodes the compound once before dispatch;
    //  OnRtcpPackets now receives an already-parsed list, so there is nothing to malform at this level.)

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

    // ── Sent-feedback counters (getStats NackCount / PliCount) ───────────────────

    [Fact]
    public void Loss_counts_the_sent_nack_and_pli()
    {
        var feedback = CreateFeedback(() => { }, out _, supportsNack: true, supportsPli: true);

        feedback.OnLoss(0x7, [200, 201]);

        Assert.Equal(1, feedback.NacksSent);
        Assert.Equal(1, feedback.PlisSent);
    }

    [Fact]
    public void Throttled_pli_counts_once_but_each_nack_counts()
    {
        var feedback = CreateFeedback(() => { }, out _, supportsNack: true, supportsPli: true);

        feedback.OnLoss(0x7, [200]);
        feedback.OnLoss(0x7, [201]); // second PLI collapsed by the 500 ms throttle; the NACK is sent again

        Assert.Equal(2, feedback.NacksSent);
        Assert.Equal(1, feedback.PlisSent);
    }

    [Fact]
    public void Gated_off_feedback_is_not_counted()
    {
        var feedback = CreateFeedback(() => { }, out _, supportsNack: false, supportsPli: false);

        feedback.OnLoss(0x7, [200, 201]);

        Assert.Equal(0, feedback.NacksSent);
        Assert.Equal(0, feedback.PlisSent);
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

    [Fact]
    public void Inbound_nack_hands_the_lost_sequence_numbers_to_the_retransmit_path()
    {
        List<ushort>? requested = null;
        var feedback = CreateFeedback(() => { }, out _, onRetransmitRequested: seqs => requested = seqs.ToList());

        feedback.OnRtcpPackets([new RtcpGenericNack
        {
            SenderSsrc = 1,
            MediaSsrc = LocalSsrc,
            Entries = [new RtcpNackEntry { PacketId = 500, LostPacketBitmask = 0b0000_0000_0000_0101 }],
        }]);

        Assert.Equal((ushort[])[500, 501, 503], requested?.ToArray());
    }

    [Fact]
    public void Inbound_pli_does_not_trigger_the_retransmit_path()
    {
        var retransmits = 0;
        var feedback = CreateFeedback(() => { }, out _, onRetransmitRequested: _ => retransmits++);

        feedback.OnRtcpPackets(
            [new RtcpPictureLossIndication { SenderSsrc = 1, MediaSsrc = LocalSsrc }]);

        Assert.Equal(0, retransmits);
    }

    // ── App-driven keyframe request (RequestKeyFrameAsync) ───────────────────────

    [Fact]
    public async Task App_keyframe_request_sends_a_single_pli_naming_the_remote_ssrc()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsPli: true);
        const uint remoteSsrc = 0x0BADF00D;

        var sentPli = await feedback.RequestKeyFrameAsync(remoteSsrc);

        Assert.True(sentPli);
        var pli = Assert.IsType<RtcpPictureLossIndication>(Assert.Single(Codec.Decode(Assert.Single(sent))));
        Assert.Equal(LocalSsrc, pli.SenderSsrc);
        Assert.Equal(remoteSsrc, pli.MediaSsrc);
        Assert.Equal(1, feedback.PlisSent);
    }

    [Fact]
    public async Task App_keyframe_request_without_advertised_pli_is_a_no_op()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsPli: false);

        var sentPli = await feedback.RequestKeyFrameAsync(0x1234);

        Assert.False(sentPli);
        Assert.Empty(sent);
        Assert.Equal(0, feedback.PlisSent);
    }

    [Fact]
    public async Task App_keyframe_request_is_throttled_across_rapid_calls()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsPli: true);

        var first = await feedback.RequestKeyFrameAsync(0x1234);
        var second = await feedback.RequestKeyFrameAsync(0x1234); // within the 500 ms window → collapsed

        Assert.True(first);
        Assert.False(second);
        Assert.Single(sent);
        Assert.Equal(1, feedback.PlisSent);
    }

    [Fact]
    public async Task App_keyframe_request_shares_the_throttle_with_the_loss_pli()
    {
        var feedback = CreateFeedback(() => { }, out var sent, supportsNack: false, supportsPli: true);

        feedback.OnLoss(0x1234, [101]);                          // loss-driven PLI claims the window
        var appPli = await feedback.RequestKeyFrameAsync(0x1234); // same window → collapsed

        Assert.False(appPli);
        Assert.Single(sent);
        Assert.Equal(1, feedback.PlisSent);
    }

    private static VideoKeyFrameFeedback CreateFeedback(
        Action onKeyFrameRequested, out List<byte[]> sentDatagrams,
        bool supportsNack = false, bool supportsPli = true,
        Action<IReadOnlyList<ushort>>? onRetransmitRequested = null)
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
            // The production callback now carries which media SSRC was named (#227); these cases only count
            // requests, so the ssrc is dropped here and asserted where attribution is the subject.
            _ => onKeyFrameRequested(),
            onRetransmitRequested ?? (_ => { }),
            NullLogger.Instance,
            CancellationToken.None);
    }
}
