using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-cc feedback interpreter: rebuilds per-packet received/lost outcomes and absolute
/// arrival times from a report (reference time + cumulative 250 µs deltas), and round-trips against
/// the builder so the sender-side stage agrees with the receiver-side one.
/// </summary>
public sealed class TransportCcFeedbackInterpreterTests
{
    [Fact]
    public void Reconstructs_arrivals_and_gaps_directly()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1,
            MediaSsrc = 2,
            ReferenceTimeTicks = 2, // 2 × 64 ms = 128 000 µs base
            FeedbackPacketCount = 0,
            Statuses =
            [
                new RtcpTransportFeedbackStatus { SequenceNumber = 10, Received = true, DeltaTicks = 4 },  // +1 000 → 129 000
                new RtcpTransportFeedbackStatus { SequenceNumber = 11, Received = false },
                new RtcpTransportFeedbackStatus { SequenceNumber = 12, Received = true, DeltaTicks = 8 },  // +2 000 → 131 000
            ],
        };

        var results = TransportCcFeedbackInterpreter.Interpret(feedback);

        Assert.Equal([(ushort)10, 11, 12], results.Select(r => r.SequenceNumber).ToArray());
        Assert.Equal([true, false, true], results.Select(r => r.Received).ToArray());
        Assert.Equal(129_000, results[0].ArrivalMicros);
        Assert.Equal(131_000, results[2].ArrivalMicros);
    }

    [Fact]
    public void Round_trips_arrival_times_through_the_builder()
    {
        // Arrivals at exact 250 µs multiples so the quantisation is lossless.
        var arrivals = new[]
        {
            new TransportCcArrival { SequenceNumber = 100, ArrivalTimestamp = 64_000 },
            new TransportCcArrival { SequenceNumber = 101, ArrivalTimestamp = 64_250 },
            new TransportCcArrival { SequenceNumber = 103, ArrivalTimestamp = 64_750 }, // 102 missing
        };
        var feedback = TransportCcFeedbackBuilder.Build(
            arrivals, senderSsrc: 1, mediaSsrc: 2, feedbackPacketCount: 0, epochTimestamp: 0, ticksPerSecond: 1_000_000);

        var results = TransportCcFeedbackInterpreter.Interpret(feedback);

        Assert.Equal([(ushort)100, 101, 102, 103], results.Select(r => r.SequenceNumber).ToArray());
        Assert.Equal([true, true, false, true], results.Select(r => r.Received).ToArray());
        var received = results.Where(r => r.Received).ToArray();
        Assert.Equal(64_000, received[0].ArrivalMicros);
        Assert.Equal(64_250, received[1].ArrivalMicros);
        Assert.Equal(64_750, received[2].ArrivalMicros);
    }

    [Fact]
    public void Reconstructs_a_negative_reference_time()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1,
            MediaSsrc = 2,
            ReferenceTimeTicks = -1, // -64 000 µs base
            FeedbackPacketCount = 0,
            Statuses = [new RtcpTransportFeedbackStatus { SequenceNumber = 0, Received = true, DeltaTicks = 4 }],
        };

        var results = TransportCcFeedbackInterpreter.Interpret(feedback);

        Assert.Equal(-63_000, results[0].ArrivalMicros); // -64 000 + 4 × 250
    }
}
