using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Receive-side transport-cc feedback sender: records stamped inbound arrivals and, on the periodic flush,
/// builds and sends a decodable transport-cc RTCP report (with a monotonic feedback counter) while ignoring
/// packets that carry no (or a different) transport-cc extension. The flush is timer-driven and decoupled from
/// arrivals (#14 #9: a packet-triggered send never flushes the tail once the stream pauses); the deterministic
/// report tests drive the flush directly, and a loop test proves the timer flushes without a further packet.
/// </summary>
public sealed class TransportCcFeedbackSenderTests
{
    private const byte ExtId = 5;
    private const long Frequency = 1_000_000; // arrival ticks are microseconds
    private const uint LocalSsrc = 0xAAAA;
    private const uint RemoteSsrc = 0x1234;

    private static TransportCcFeedbackSender Sender(
        List<byte[]> sent, Func<long> clock, ILogger? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(new RtcpPacketCodec(), ExtId, LocalSsrc,
            (data, _) => { sent.Add(data.ToArray()); return ValueTask.CompletedTask; },
            clock, Frequency, logger ?? NullLogger.Instance, CancellationToken.None, delay);

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
    public void Records_arrivals_and_sends_a_decodable_report_on_flush()
    {
        var sent = new List<byte[]>();
        long clock = 0;
        var sender = Sender(sent, () => clock);

        clock = 0;      sender.OnVideoPacketReceived(Stamped(ExtId, 100, 1));
        clock = 50_000; sender.OnVideoPacketReceived(Stamped(ExtId, 101, 2));
        clock = 100_000; sender.OnVideoPacketReceived(Stamped(ExtId, 102, 3));
        Assert.Empty(sent); // nothing until a flush

        sender.FlushForTest();

        var feedback = Decode(Assert.Single(sent));
        Assert.Equal(LocalSsrc, feedback.SenderSsrc);
        Assert.Equal(RemoteSsrc, feedback.MediaSsrc);
        Assert.Equal([(ushort)100, 101, 102], feedback.Statuses.Select(s => s.SequenceNumber).ToArray());
        Assert.All(feedback.Statuses, s => Assert.True(s.Received));
    }

    [Fact]
    public void A_flush_before_any_packet_sends_nothing()
    {
        var sent = new List<byte[]>();
        var sender = Sender(sent, () => 0);

        sender.FlushForTest();

        Assert.Empty(sent);
    }

    [Fact]
    public void Ignores_packets_without_the_transport_cc_extension()
    {
        var sent = new List<byte[]>();
        var sender = Sender(sent, () => 0);

        sender.OnVideoPacketReceived(new RtpPacket { PayloadType = 96, SequenceNumber = 1 });
        sender.OnVideoPacketReceived(new RtpPacket { PayloadType = 96, SequenceNumber = 2 });
        sender.FlushForTest();

        Assert.Empty(sent);
    }

    [Fact]
    public void Ignores_a_different_extension_id()
    {
        var sent = new List<byte[]>();
        var sender = Sender(sent, () => 0);

        sender.OnVideoPacketReceived(Stamped(7, 100, 1)); // sender expects id 5
        sender.OnVideoPacketReceived(Stamped(7, 101, 2));
        sender.FlushForTest();

        Assert.Empty(sent);
    }

    [Fact]
    public void Skips_a_batch_whose_delta_exceeds_the_representable_range()
    {
        var sent = new List<byte[]>();
        long clock = 0;
        var sender = Sender(sent, () => clock);

        // Two received packets ~10 s apart → a receive delta beyond the signed-int16 range the wire
        // format allows: the report cannot be encoded and must be skipped, not crash the send path.
        clock = 0;          sender.OnVideoPacketReceived(Stamped(ExtId, 100, 1));
        clock = 10_000_000; sender.OnVideoPacketReceived(Stamped(ExtId, 101, 2));
        sender.FlushForTest();

        Assert.Empty(sent);
    }

    [Fact]
    public void Increments_the_feedback_packet_count_across_reports()
    {
        var sent = new List<byte[]>();
        long clock = 0;
        var sender = Sender(sent, () => clock);

        clock = 0;       sender.OnVideoPacketReceived(Stamped(ExtId, 100, 1));
        sender.FlushForTest();                                       // report #0
        clock = 150_000; sender.OnVideoPacketReceived(Stamped(ExtId, 101, 2));
        sender.FlushForTest();                                       // report #1

        Assert.Equal(2, sent.Count);
        Assert.Equal(0, Decode(sent[0]).FeedbackPacketCount);
        Assert.Equal(1, Decode(sent[1]).FeedbackPacketCount);
    }

    [Fact]
    public void Overflow_of_the_arrival_buffer_still_sends_and_is_logged()
    {
        var sent = new List<byte[]>();
        var logger = new CapturingLogger();
        var sender = Sender(sent, () => 0, logger);

        // More arrivals than the ring buffer holds (1024) before a flush: the oldest are overwritten.
        // The report still goes out (no crash) and the overflow is logged once.
        for (ushort i = 0; i < 1100; i++)
            sender.OnVideoPacketReceived(Stamped(ExtId, i, i));
        sender.FlushForTest();

        Assert.NotEmpty(sent);
        Assert.Contains(LogLevel.Debug, logger.Levels);
    }

    [Fact]
    public async Task The_loop_flushes_pending_feedback_on_a_tick_without_a_further_packet()
    {
        // The #9 tail case: arrivals are recorded, then the stream goes quiet. A packet-triggered sender would
        // never flush them; the timer loop must. One tick fires, then the delay blocks so exactly one flush runs.
        var sent = new List<byte[]>();
        var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticks = 0;

        await using var sender = new TransportCcFeedbackSender(
            new RtcpPacketCodec(), ExtId, LocalSsrc,
            (data, _) => { sent.Add(data.ToArray()); sendGate.TrySetResult(); return ValueTask.CompletedTask; },
            () => 0, Frequency, NullLogger.Instance, CancellationToken.None,
            delay: (_, ct) => Interlocked.Increment(ref ticks) == 1 ? Task.CompletedTask : Task.Delay(Timeout.Infinite, ct));

        sender.OnVideoPacketReceived(Stamped(ExtId, 100, 1));
        sender.OnVideoPacketReceived(Stamped(ExtId, 101, 2));
        sender.Start(); // no further packet — the loop must flush on the tick

        await sendGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([(ushort)100, 101], Decode(Assert.Single(sent)).Statuses.Select(s => s.SequenceNumber).ToArray());
    }

    [Fact]
    public async Task DisposeAsync_stops_the_loop()
    {
        var sender = Sender(new List<byte[]>(), () => 0,
            delay: (_, ct) => Task.Delay(Timeout.Infinite, ct)); // loop parks in the delay
        sender.Start();

        var stopped = await Record.ExceptionAsync(async () => await sender.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Null(stopped);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
