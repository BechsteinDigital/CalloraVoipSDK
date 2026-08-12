using System.Linq;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-4: transport-cc packet-status symbols are indexed by <em>sequence number</em> relative to the base,
/// not by list position (draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1.3 — the receiver walks base,
/// base+1, base+2 … as it consumes symbols). The status list carries only the packets we have something to say
/// about; every sequence number in between is implicitly "not received" and must still occupy its slot.
/// <para>
/// Laying the list out contiguously made a report about 10 and 12 decode as 10 and 11 — every status after a
/// gap was attributed to the wrong packet, so the congestion estimator consumed arrival deltas belonging to
/// other packets. Silent, and wrong in the direction that matters: it corrupts the input to bandwidth
/// estimation rather than failing loudly.
/// </para>
/// </summary>
public sealed class RtcpTransportFeedbackGapTests
{
    private static RtcpTransportFeedbackStatus Received(ushort seq, int deltaTicks = 4) =>
        new() { SequenceNumber = seq, Received = true, DeltaTicks = deltaTicks };

    private static RtcpTransportFeedback Feedback(params RtcpTransportFeedbackStatus[] statuses) => new()
    {
        SenderSsrc = 0x11111111,
        MediaSsrc = 0x22222222,
        ReferenceTimeTicks = 1000,
        FeedbackPacketCount = 1,
        Statuses = statuses,
    };

    private static RtcpTransportFeedback RoundTrip(RtcpTransportFeedback feedback)
    {
        var wire = RtcpTransportFeedbackCodec.Encode(feedback);
        // Skip the 4-byte RTCP header and the SSRC pair to get the FCI body the decoder expects.
        var fci = wire.AsSpan(12);
        return RtcpTransportFeedbackCodec.Decode(feedback.SenderSsrc, feedback.MediaSsrc, fci);
    }

    [Fact]
    public void A_gap_between_reported_packets_survives_the_round_trip()
    {
        // The exact case from the review: report 10 and 12; 11 was never received.
        var decoded = RoundTrip(Feedback(Received(10), Received(12)));

        var received = decoded.Statuses.Where(s => s.Received).Select(s => s.SequenceNumber).ToArray();
        Assert.Equal(new ushort[] { 10, 12 }, received);
    }

    [Fact]
    public void The_missing_sequence_number_is_reported_as_not_received()
    {
        var decoded = RoundTrip(Feedback(Received(10), Received(12)));

        var eleven = Assert.Single(decoded.Statuses.Where(s => s.SequenceNumber == 11));
        Assert.False(eleven.Received);
    }

    [Fact]
    public void Deltas_stay_with_their_own_packets_across_a_gap()
    {
        // The damaging part: a shifted layout hands packet 12's arrival delta to packet 11.
        var decoded = RoundTrip(Feedback(Received(10, deltaTicks: 4), Received(12, deltaTicks: 40)));

        Assert.Equal(4, decoded.Statuses.Single(s => s.SequenceNumber == 10).DeltaTicks);
        Assert.Equal(40, decoded.Statuses.Single(s => s.SequenceNumber == 12).DeltaTicks);
    }

    [Fact]
    public void A_run_of_missing_packets_is_preserved()
    {
        var decoded = RoundTrip(Feedback(Received(100), Received(110)));

        Assert.Equal(11, decoded.Statuses.Count);   // 100..110 inclusive
        Assert.Equal(
            new ushort[] { 100, 110 },
            decoded.Statuses.Where(s => s.Received).Select(s => s.SequenceNumber).ToArray());
    }

    [Fact]
    public void A_contiguous_report_is_unchanged()
    {
        // The case that already worked must keep working — the fix is about gaps, not about renumbering.
        var decoded = RoundTrip(Feedback(Received(7, 1), Received(8, 2), Received(9, 3)));

        Assert.Equal(
            new ushort[] { 7, 8, 9 },
            decoded.Statuses.Select(s => s.SequenceNumber).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, decoded.Statuses.Select(s => s.DeltaTicks).ToArray());
    }

    [Fact]
    public void A_gap_across_the_sequence_wrap_is_measured_as_a_short_distance()
    {
        // 65535 -> 1 is a distance of two, not a 65 534-slot span (draft §3.1.1 wrap).
        var decoded = RoundTrip(Feedback(Received(65535), Received(1)));

        Assert.Equal(3, decoded.Statuses.Count);   // 65535, 0, 1
        Assert.Equal(
            new ushort[] { 65535, 1 },
            decoded.Statuses.Where(s => s.Received).Select(s => s.SequenceNumber).ToArray());
    }

    [Fact]
    public void A_span_beyond_the_status_cap_is_refused_rather_than_emitted()
    {
        // A caller reporting two packets 5000 apart would otherwise emit ~715 chunks for two data points.
        var ex = Assert.Throws<ArgumentException>(
            () => RtcpTransportFeedbackCodec.Encode(Feedback(Received(0), Received(5000))));

        Assert.Contains("cap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
