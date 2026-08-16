using CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 3550 §6.4.1 defines the inter-arrival jitter over |d(i)|, the absolute transit
/// difference. The difference is a signed 32-bit RTP-clock delta, so the absolute value has
/// to be taken in a wider type: int.MinValue has no positive counterpart in int and the
/// arithmetic is unchecked, so negating it in place leaves it negative. A remote timestamp
/// jump of exactly 0x80000000 hits that case (#161 P2-9).
/// </summary>
public sealed class JitterBufferTimestampJumpTests
{
    private static RtpPacket Packet(ushort seq, uint ts) => new()
    {
        PayloadType = 0,
        SequenceNumber = seq,
        Timestamp = ts,
        Ssrc = 0x1234,
        Payload = new byte[160]
    };

    [Fact]
    public void Timestamp_jump_of_int_min_value_does_not_produce_negative_jitter()
    {
        var buffer = new JitterBuffer(new JitterBufferOptions { ClockRate = 8000, InitialDelayMs = 40 });
        var t0 = DateTimeOffset.UnixEpoch;

        buffer.Add(Packet(0, 0), t0);

        // Same arrival instant, timestamp jumped by 2^31: the transit difference is exactly
        // int.MinValue. Before the fix the estimate went to -16,777,216 ms.
        buffer.Add(Packet(1, 0x8000_0000), t0);

        Assert.True(buffer.EstimatedJitterMs >= 0,
            $"jitter estimate went negative: {buffer.EstimatedJitterMs} ms");
    }

    [Fact]
    public void Negative_transit_difference_is_still_taken_as_an_absolute_value()
    {
        var buffer = new JitterBuffer(new JitterBufferOptions { ClockRate = 8000, InitialDelayMs = 40 });
        var t0 = DateTimeOffset.UnixEpoch;

        // Two packets one frame apart in RTP time but arriving together: transit drops by one
        // frame (160 units), so |d| = 160 and J = 160/16 = 10 units = 1.25 ms at 8 kHz.
        buffer.Add(Packet(0, 0), t0);
        buffer.Add(Packet(1, 160), t0);

        Assert.Equal(1.25, buffer.EstimatedJitterMs, 3);
    }
}
