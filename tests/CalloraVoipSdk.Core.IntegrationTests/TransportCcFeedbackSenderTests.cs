using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Receive-side transport-cc feedback sender: records stamped inbound arrivals and, once a feedback
/// interval elapses, builds and sends a decodable transport-cc RTCP report — with a monotonic
/// feedback counter — while ignoring packets that carry no (or a different) transport-cc extension.
/// Driven by an injected clock so the interval logic is deterministic.
/// </summary>
public sealed class TransportCcFeedbackSenderTests
{
    private const byte ExtId = 5;
    private const long Frequency = 1_000_000; // arrival ticks are microseconds → interval = 100_000
    private const uint LocalSsrc = 0xAAAA;
    private const uint RemoteSsrc = 0x1234;

    private static TransportCcFeedbackSender Sender(List<byte[]> sent, Func<long> clock) =>
        new(new RtcpPacketCodec(), ExtId, LocalSsrc,
            (data, _) => { sent.Add(data.ToArray()); return ValueTask.CompletedTask; },
            clock, Frequency, NullLogger.Instance, CancellationToken.None);

    private static RtpPacket Stamped(byte extId, ushort transportSeq, ushort rtpSeq) => new()
    {
        PayloadType = 96,
        SequenceNumber = rtpSeq,
        Ssrc = RemoteSsrc,
        HeaderExtension = OneByteRtpHeaderExtensions.Encode(
            [OneByteRtpHeaderExtensions.TransportSequenceNumber(extId, transportSeq)]),
    };

    private static RtcpTransportFeedback Decode(byte[] datagram) =>
        Assert.IsType<RtcpTransportFeedback>(Assert.Single(new RtcpPacketCodec().Decode(datagram)));

    [Fact]
    public void Sends_a_decodable_report_after_the_interval_but_not_before()
    {
        var sent = new List<byte[]>();
        long clock = 0;
        var sender = Sender(sent, () => clock);

        clock = 0;       sender.OnVideoPacketReceived(Stamped(ExtId, 100, 1));
        clock = 50_000;  sender.OnVideoPacketReceived(Stamped(ExtId, 101, 2)); // 50 ms < interval
        Assert.Empty(sent);

        clock = 150_000; sender.OnVideoPacketReceived(Stamped(ExtId, 102, 3)); // 150 ms ≥ interval

        var feedback = Decode(Assert.Single(sent));
        Assert.Equal(LocalSsrc, feedback.SenderSsrc);
        Assert.Equal(RemoteSsrc, feedback.MediaSsrc);
        Assert.Equal([(ushort)100, 101, 102], feedback.Statuses.Select(s => s.SequenceNumber).ToArray());
        Assert.All(feedback.Statuses, s => Assert.True(s.Received));
    }

    [Fact]
    public void Ignores_packets_without_the_transport_cc_extension()
    {
        var sent = new List<byte[]>();
        long clock = 0;
        var sender = Sender(sent, () => clock);

        sender.OnVideoPacketReceived(new RtpPacket { PayloadType = 96, SequenceNumber = 1 });
        clock = 500_000;
        sender.OnVideoPacketReceived(new RtpPacket { PayloadType = 96, SequenceNumber = 2 });

        Assert.Empty(sent);
    }

    [Fact]
    public void Ignores_a_different_extension_id()
    {
        var sent = new List<byte[]>();
        long clock = 0;
        var sender = Sender(sent, () => clock);

        clock = 0;       sender.OnVideoPacketReceived(Stamped(7, 100, 1)); // sender expects id 5
        clock = 500_000; sender.OnVideoPacketReceived(Stamped(7, 101, 2));

        Assert.Empty(sent);
    }

    [Fact]
    public void Skips_a_batch_whose_delta_exceeds_the_representable_range()
    {
        var sent = new List<byte[]>();
        long clock = 0;
        var sender = Sender(sent, () => clock);

        // Two received packets ~10 s apart → a receive delta beyond the signed-int16 range the wire
        // format allows: the report cannot be encoded and must be skipped, not crash the receive path.
        clock = 0;          sender.OnVideoPacketReceived(Stamped(ExtId, 100, 1));
        clock = 10_000_000; sender.OnVideoPacketReceived(Stamped(ExtId, 101, 2));

        Assert.Empty(sent);
    }

    [Fact]
    public void Increments_the_feedback_packet_count_across_reports()
    {
        var sent = new List<byte[]>();
        long clock = 0;
        var sender = Sender(sent, () => clock);

        clock = 0;       sender.OnVideoPacketReceived(Stamped(ExtId, 100, 1));
        clock = 150_000; sender.OnVideoPacketReceived(Stamped(ExtId, 101, 2)); // report #1
        clock = 300_000; sender.OnVideoPacketReceived(Stamped(ExtId, 102, 3)); // report #2

        Assert.Equal(2, sent.Count);
        Assert.Equal(0, Decode(sent[0]).FeedbackPacketCount);
        Assert.Equal(1, Decode(sent[1]).FeedbackPacketCount);
    }
}
