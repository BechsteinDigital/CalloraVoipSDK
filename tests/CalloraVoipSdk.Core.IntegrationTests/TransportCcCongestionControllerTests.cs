using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Sender-side transport-cc congestion controller: records stamped outgoing packets, then on an
/// inbound feedback report correlates delay gradients and folds in loss — end to end from send +
/// report to the coarse congestion signal, staying quiet when nothing correlates and skipping
/// malformed datagrams.
/// </summary>
public sealed class TransportCcCongestionControllerTests
{
    private const byte ExtId = 5;

    private static TransportCcCongestionController Controller(Func<long> clock) =>
        new(ExtId, new RtcpPacketCodec(), new TransportCcSendHistory(64),
            new TransportCcDelayTrendEstimator(1.0, 100), new TransportCcLossEstimator(1.0),
            clock, 1_000_000, NullLogger.Instance);

    private static RtpPacket Stamped(ushort seq) => new()
    {
        PayloadType = 96,
        SequenceNumber = seq,
        HeaderExtension = OneByteRtpHeaderExtensions.Encode(
            [OneByteRtpHeaderExtensions.TransportSequenceNumber(ExtId, seq)]),
    };

    private static TransportCcArrival Arrival(ushort seq, long micros) =>
        new() { SequenceNumber = seq, ArrivalTimestamp = micros };

    private static byte[] FeedbackDatagram(params TransportCcArrival[] arrivals)
    {
        var feedback = TransportCcFeedbackBuilder.Build(
            arrivals, senderSsrc: 0xAAAA, mediaSsrc: 0x1234, feedbackPacketCount: 0,
            epochTimestamp: 0, ticksPerSecond: 1_000_000);
        return new RtcpPacketCodec().Encode([feedback]);
    }

    [Fact]
    public void Rising_one_way_delay_drives_the_signal_to_overusing()
    {
        long clock = 0;
        var controller = Controller(() => clock);

        clock = 0;     controller.OnPacketSent(Stamped(1));
        clock = 1_000; controller.OnPacketSent(Stamped(2));
        clock = 2_000; controller.OnPacketSent(Stamped(3));

        // Peer inter-arrival 1250 µs vs inter-send 1000 µs → +250 µs gradient per packet.
        controller.OnControlDatagram(
            FeedbackDatagram(Arrival(1, 10_000), Arrival(2, 11_250), Arrival(3, 12_500)));

        Assert.Equal(CongestionSignal.Overusing, controller.Signal);
        Assert.True(controller.DelayTrendMicros > 100);
    }

    [Fact]
    public void Constant_one_way_delay_stays_normal()
    {
        long clock = 0;
        var controller = Controller(() => clock);

        clock = 0;     controller.OnPacketSent(Stamped(1));
        clock = 1_000; controller.OnPacketSent(Stamped(2));
        clock = 2_000; controller.OnPacketSent(Stamped(3));

        // Inter-arrival 1000 µs == inter-send 1000 µs → zero gradient.
        controller.OnControlDatagram(
            FeedbackDatagram(Arrival(1, 10_000), Arrival(2, 11_000), Arrival(3, 12_000)));

        Assert.Equal(CongestionSignal.Normal, controller.Signal);
    }

    [Fact]
    public void A_reported_gap_raises_the_loss_ratio()
    {
        long clock = 0;
        var controller = Controller(() => clock);

        clock = 0;     controller.OnPacketSent(Stamped(1));
        clock = 1_000; controller.OnPacketSent(Stamped(3));

        // Arrivals for 1 and 3 only → the builder fills seq 2 as not-received: 1 of 3 lost.
        controller.OnControlDatagram(FeedbackDatagram(Arrival(1, 10_000), Arrival(3, 12_000)));

        Assert.True(controller.LossRatio > 0);
    }

    [Fact]
    public void Feedback_for_never_sent_packets_leaves_the_delay_signal_at_rest()
    {
        long clock = 0;
        var controller = Controller(() => clock);

        // No OnPacketSent → send history empty → nothing correlates → trend unchanged.
        controller.OnControlDatagram(FeedbackDatagram(Arrival(1, 10_000), Arrival(2, 11_500)));

        Assert.Equal(CongestionSignal.Normal, controller.Signal);
        Assert.Equal(0, controller.DelayTrendMicros, 6);
    }

    [Fact]
    public void A_malformed_datagram_is_dropped_without_disturbing_the_signal()
    {
        long clock = 0;
        var controller = Controller(() => clock);

        controller.OnControlDatagram([0xFF, 0xFF, 0xFF]); // truncated RTCP header

        Assert.Equal(CongestionSignal.Normal, controller.Signal);
    }

    [Fact]
    public void Rejects_invalid_construction()
    {
        var codec = new RtcpPacketCodec();
        var history = new TransportCcSendHistory(64);
        var trend = new TransportCcDelayTrendEstimator(0.5, 100);
        var loss = new TransportCcLossEstimator(0.5);

        Assert.Throws<ArgumentNullException>(() => new TransportCcCongestionController(
            ExtId, codec, history, trend, loss, null!, 1_000_000, NullLogger.Instance));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransportCcCongestionController(
            ExtId, codec, history, trend, loss, () => 0, 0, NullLogger.Instance));
    }
}
