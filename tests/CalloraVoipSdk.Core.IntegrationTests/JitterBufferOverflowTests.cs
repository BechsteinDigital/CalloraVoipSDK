using CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The jitter buffer is capacity-bounded: once <see cref="JitterBufferOptions.Capacity"/>
/// packets are held it rejects further arrivals (dropping the newest) rather than flushing the
/// whole buffer. This locks that overflow policy — a flush-all would dump a second of audio,
/// and an unbounded buffer would grow without limit under a stalled playout / heavy reordering.
/// </summary>
public sealed class JitterBufferOverflowTests
{
    private static RtpPacket Packet(ushort seq, uint timestamp) => new()
    {
        Ssrc = 0x1234_5678,
        SequenceNumber = seq,
        Timestamp = timestamp,
        PayloadType = 0,
        Payload = new byte[160],
    };

    [Fact]
    public void Packets_past_capacity_are_dropped_and_the_buffer_is_not_flushed()
    {
        var options = new JitterBufferOptions();
        var buffer = new JitterBuffer(options);
        var arrival = DateTimeOffset.UnixEpoch;

        var queued = 0;
        for (ushort i = 0; i < options.Capacity; i++)
        {
            if (buffer.Add(Packet(i, (uint)(i * 160)), arrival) == JitterBufferAddResult.Queued)
                queued++;
        }

        Assert.Equal(options.Capacity, queued);

        // The next arrival overflows (dropped, not queued)...
        Assert.Equal(
            JitterBufferAddResult.Overflow,
            buffer.Add(Packet((ushort)options.Capacity, (uint)(options.Capacity * 160)), arrival));

        // ...and the buffer stays full rather than having flushed — a further arrival also overflows.
        Assert.Equal(
            JitterBufferAddResult.Overflow,
            buffer.Add(Packet((ushort)(options.Capacity + 1), (uint)((options.Capacity + 1) * 160)), arrival));
    }
}
