using CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A stalled RTP timestamp (Δts==0) — comfort noise or an audio packet repeating the same ts —
/// must not poison the RFC 3550 §6.4.1 jitter estimate nor ratchet the adaptive delay up via late
/// drops. Genuine audio never repeats a timestamp, so such repeats are treated as playout-redundant
/// duplicates. (RFC 4733 telephone-events are demuxed before the buffer and never reach it.)
/// </summary>
public sealed class JitterBufferStalledTimestampTests
{
    private static RtpPacket Packet(ushort seq, uint ts) => new()
    {
        PayloadType = 0,
        SequenceNumber = seq,
        Timestamp = ts,
        Ssrc = 0x1234,
        Payload = new byte[160]
    };

    private static JitterBuffer Primed(out DateTimeOffset t0, out double jitter, out double delay)
    {
        var buffer = new JitterBuffer(new JitterBufferOptions { ClockRate = 8000, InitialDelayMs = 40 });
        t0 = DateTimeOffset.UnixEpoch;
        // Normal advancing-timestamp stream arriving on time (20 ms cadence @ 8 kHz).
        for (ushort seq = 0; seq < 5; seq++)
            buffer.Add(Packet(seq, (uint)(seq * 160)), t0.AddMilliseconds(seq * 20));
        jitter = buffer.EstimatedJitterMs;
        delay = buffer.CurrentDelayMs;
        return buffer;
    }

    [Fact]
    public void Stalled_timestamp_burst_does_not_spike_jitter_or_ratchet_delay()
    {
        var buffer = Primed(out var t0, out var jitterBefore, out var delayBefore);

        // A burst repeating the last timestamp (4*160), spread over real arrival time.
        // Before the fix this spiked jitter and ratcheted the delay via 5 late drops.
        for (ushort i = 0; i < 5; i++)
        {
            var result = buffer.Add(Packet((ushort)(100 + i), 4 * 160), t0.AddMilliseconds(200 + i * 20));
            Assert.Equal(JitterBufferAddResult.Duplicate, result); // playout-redundant, not a late drop
        }

        Assert.Equal(jitterBefore, buffer.EstimatedJitterMs, 3); // no spike
        Assert.True(buffer.CurrentDelayMs <= delayBefore + 0.001,
            $"adaptive delay ratcheted up: {delayBefore} -> {buffer.CurrentDelayMs}");
    }

    [Fact]
    public void Advancing_timestamps_are_queued_not_treated_as_stalled()
    {
        var buffer = new JitterBuffer(new JitterBufferOptions { ClockRate = 8000, InitialDelayMs = 40 });
        var t0 = DateTimeOffset.UnixEpoch;

        for (ushort seq = 0; seq < 5; seq++)
        {
            var result = buffer.Add(Packet(seq, (uint)(seq * 160)), t0.AddMilliseconds(seq * 20));
            Assert.Equal(JitterBufferAddResult.Queued, result);
        }
    }
}
