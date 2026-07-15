using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-cc feedback builder: turns a batch of recorded arrivals into a
/// <c>RtcpTransportFeedback</c> model — sequence ordering with 16-bit unwrap, not-received gap
/// filling, reference-time (64 ms) and receive-delta (250 µs) quantisation, duplicate collapsing,
/// negative deltas under reordering, and a full round trip through the wire codec.
/// </summary>
public sealed class TransportCcFeedbackBuilderTests
{
    private const long MicrosecondFrequency = 1_000_000; // arrival ticks are microseconds
    private const long Epoch = 0;

    private static TransportCcArrival Arrival(ushort seq, long ticks)
        => new() { SequenceNumber = seq, ArrivalTimestamp = ticks };

    private static RtcpTransportFeedback Build(params TransportCcArrival[] arrivals)
        => TransportCcFeedbackBuilder.Build(arrivals, 0x1, 0x2, 0, Epoch, MicrosecondFrequency);

    [Fact]
    public void Fills_gaps_and_quantises_deltas()
    {
        // seq 102 is missing → reported as not received; arrivals are exact multiples of 250 µs.
        var feedback = Build(Arrival(100, 64_000), Arrival(101, 64_250), Arrival(103, 64_750));

        Assert.Equal(4, feedback.Statuses.Count);
        Assert.Equal(100, feedback.Statuses[0].SequenceNumber);
        Assert.Equal(1, feedback.ReferenceTimeTicks); // 64000 µs / 64 ms
        Assert.Equal([(ushort)100, 101, 102, 103], feedback.Statuses.Select(s => s.SequenceNumber).ToArray());
        Assert.Equal([true, true, false, true], feedback.Statuses.Select(s => s.Received).ToArray());
        Assert.Equal(0, feedback.Statuses[0].DeltaTicks);
        Assert.Equal(1, feedback.Statuses[1].DeltaTicks);
        Assert.Equal(2, feedback.Statuses[3].DeltaTicks);
    }

    [Fact]
    public void Unwraps_sequence_numbers_across_the_16bit_boundary()
    {
        var feedback = Build(Arrival(65535, 64_000), Arrival(0, 64_250), Arrival(1, 64_500));

        Assert.Equal([(ushort)65535, 0, 1], feedback.Statuses.Select(s => s.SequenceNumber).ToArray());
        Assert.All(feedback.Statuses, s => Assert.True(s.Received));
        Assert.Equal([0, 1, 1], feedback.Statuses.Select(s => s.DeltaTicks).ToArray());
    }

    [Fact]
    public void Orders_by_sequence_when_the_first_recorded_arrival_is_mid_batch()
    {
        // Anchor (seq 1, recorded first) is not the lowest sequence; base must still resolve to
        // seq 65535 after unwrap, and the statuses must come out in ascending sequence order.
        var feedback = Build(Arrival(1, 64_500), Arrival(65535, 64_000), Arrival(0, 64_250));

        Assert.Equal([(ushort)65535, 0, 1], feedback.Statuses.Select(s => s.SequenceNumber).ToArray());
        Assert.All(feedback.Statuses, s => Assert.True(s.Received));
    }

    [Fact]
    public void Produces_a_negative_delta_for_a_reordered_arrival()
    {
        // seq 11 arrived 250 µs before seq 10 (reordering) → negative delta.
        var feedback = Build(Arrival(10, 64_000), Arrival(11, 63_750));

        Assert.Equal(0, feedback.Statuses[0].DeltaTicks);
        Assert.Equal(-1, feedback.Statuses[1].DeltaTicks);
    }

    [Fact]
    public void Collapses_duplicates_keeping_the_earliest_arrival()
    {
        var feedback = Build(Arrival(5, 2_000), Arrival(5, 1_000));

        var status = Assert.Single(feedback.Statuses);
        Assert.Equal(5, status.SequenceNumber);
        Assert.Equal(0, feedback.ReferenceTimeTicks); // floor(1000 / 64000)
        Assert.Equal(4, status.DeltaTicks);           // 1000 µs / 250 µs, from the earliest arrival
    }

    [Fact]
    public void Round_trips_through_the_wire_codec()
    {
        var feedback = Build(Arrival(200, 128_000), Arrival(201, 128_250), Arrival(203, 128_750));

        var wire = RtcpTransportFeedbackCodec.Encode(feedback);
        var senderSsrc = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(4));
        var mediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(8));
        var decoded = RtcpTransportFeedbackCodec.Decode(senderSsrc, mediaSsrc, wire.AsSpan(12));

        Assert.Equal(feedback.ReferenceTimeTicks, decoded.ReferenceTimeTicks);
        Assert.Equal(
            feedback.Statuses.Select(s => (s.SequenceNumber, s.Received, s.Received ? s.DeltaTicks : 0)),
            decoded.Statuses.Select(s => (s.SequenceNumber, s.Received, s.Received ? s.DeltaTicks : 0)));
    }

    [Fact]
    public void Empty_batch_is_rejected()
        => Assert.Throws<ArgumentException>(
            () => TransportCcFeedbackBuilder.Build([], 1, 2, 0, Epoch, MicrosecondFrequency));

    [Fact]
    public void Non_positive_frequency_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => TransportCcFeedbackBuilder.Build([Arrival(1, 0)], 1, 2, 0, Epoch, 0));
}
