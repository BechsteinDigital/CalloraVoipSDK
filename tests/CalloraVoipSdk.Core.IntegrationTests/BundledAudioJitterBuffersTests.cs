using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Buffering inbound audio before it is raised, so a consumer that mixes gets a steady cadence.
/// </summary>
/// <remarks>
/// A forwarding consumer needs none of this — the browser at the far end has its own jitter buffer. A
/// mixing one has the opposite problem: it must produce a frame every frame interval from whatever each
/// source delivered by then. Handed raw arrivals it reads a burst as one usable frame and the rest as
/// silence, which is what a caller hears as audio that stops after every pause. Opus DTX makes that the
/// normal case: nothing is sent while nobody speaks, and the packets after the pause arrive together.
/// </remarks>
public sealed class BundledAudioJitterBuffersTests
{
    private const string AudioMid = "0";
    private const string SecondMid = "3";

    private static RtpPacket Packet(ushort seq, uint timestamp, uint ssrc = 0x1234_5678) => new()
    {
        Ssrc = ssrc,
        SequenceNumber = seq,
        Timestamp = timestamp,
        PayloadType = 111,
        Payload = new byte[80],
    };

    private static BundledTrackConfig Track(string mid, int clockRate = 48_000) => new()
    {
        Mid = mid,
        Ssrc = 0x1000_0000,
        PayloadType = 111,
        ClockRate = clockRate,
        SamplesPerPacket = 960,
    };

    [Fact]
    public async Task A_burst_is_released_as_separate_packets_rather_than_costing_all_but_one()
    {
        // The defect this exists for. Three packets arriving together are three frames of audio; a
        // consumer that keeps one slot per source keeps the last and loses the rest.
        var released = new List<(string Mid, ushort Seq)>();
        await using var buffers = new BundledAudioJitterBuffers(
            [Track(AudioMid)],
            TimeSpan.FromMilliseconds(5),
            initialDelayMs: 20,
            (mid, packet) => { lock (released) released.Add((mid, packet.SequenceNumber)); },
            NullLogger.Instance);

        for (ushort i = 0; i < 3; i++)
        {
            Assert.True(buffers.TryAdd(AudioMid, Packet(i, (uint)(i * 960))));
        }

        await WaitUntilAsync(() => Count(released) >= 3);

        lock (released)
        {
            Assert.Equal([(AudioMid, (ushort)0), (AudioMid, (ushort)1), (AudioMid, (ushort)2)], released);
        }
    }

    [Fact]
    public async Task Packets_are_released_in_sequence_even_when_they_arrive_out_of_order()
    {
        // Reordering is what a jitter buffer is named after, and a mixer cannot undo it: it consumes
        // whatever it is handed, in the order it is handed it.
        //
        // The first arrival anchors the playout schedule, so the swap is between the two that follow it.
        // A packet that arrives after its own playout instant has passed is late by definition and no
        // buffer can place it — starting the stream with its latest packet would test that instead.
        var released = new List<ushort>();
        await using var buffers = new BundledAudioJitterBuffers(
            [Track(AudioMid)],
            TimeSpan.FromMilliseconds(5),
            initialDelayMs: 20,
            (_, packet) => { lock (released) released.Add(packet.SequenceNumber); },
            NullLogger.Instance);

        buffers.TryAdd(AudioMid, Packet(0, 0));
        buffers.TryAdd(AudioMid, Packet(2, 2 * 960));
        buffers.TryAdd(AudioMid, Packet(1, 960));

        await WaitUntilAsync(() => Count(released) >= 3);

        lock (released)
        {
            Assert.Equal<ushort[]>([0, 1, 2], [.. released]);
        }
    }

    [Fact]
    public async Task Each_m_line_is_buffered_on_its_own()
    {
        // Two audio m-lines are two streams with their own sequence and timestamp space. Sharing one
        // buffer would read the second stream's numbering as wild reordering of the first.
        var released = new List<(string Mid, ushort Seq)>();
        await using var buffers = new BundledAudioJitterBuffers(
            [Track(AudioMid), Track(SecondMid)],
            TimeSpan.FromMilliseconds(5),
            initialDelayMs: 20,
            (mid, packet) => { lock (released) released.Add((mid, packet.SequenceNumber)); },
            NullLogger.Instance);

        buffers.TryAdd(AudioMid, Packet(0, 0, ssrc: 0xAAAA));
        buffers.TryAdd(SecondMid, Packet(0, 0, ssrc: 0xBBBB));

        await WaitUntilAsync(() => Count(released) >= 2);

        lock (released)
        {
            Assert.Contains((AudioMid, (ushort)0), released);
            Assert.Contains((SecondMid, (ushort)0), released);
        }
    }

    [Fact]
    public async Task A_source_change_resets_the_stream_instead_of_discarding_it()
    {
        // A new SSRC brings its own sequence space. Without the reset the new stream reads as far out of
        // order and is dropped until the numbering happens to catch up — audible as a leg that goes
        // silent after a renegotiation and returns seconds later.
        var released = new List<ushort>();
        await using var buffers = new BundledAudioJitterBuffers(
            [Track(AudioMid)],
            TimeSpan.FromMilliseconds(5),
            initialDelayMs: 20,
            (_, packet) => { lock (released) released.Add(packet.SequenceNumber); },
            NullLogger.Instance);

        buffers.TryAdd(AudioMid, Packet(50_000, 500_000, ssrc: 0xAAAA));
        await WaitUntilAsync(() => Count(released) >= 1);

        buffers.TryAdd(AudioMid, Packet(7, 7 * 960, ssrc: 0xBBBB));

        await WaitUntilAsync(() => Count(released) >= 2);

        lock (released)
        {
            Assert.Contains((ushort)7, released);
        }
    }

    [Fact]
    public async Task An_unbuffered_m_line_is_refused_so_the_caller_can_pass_it_straight_through()
    {
        // A track negotiated after the buffers were built has no buffer. Answering false lets the caller
        // raise it directly; dropping it would silence a participant for want of a buffer.
        await using var buffers = new BundledAudioJitterBuffers(
            [Track(AudioMid)],
            TimeSpan.FromMilliseconds(5),
            initialDelayMs: 20,
            (_, _) => { },
            NullLogger.Instance);

        Assert.False(buffers.TryAdd("99", Packet(0, 0)));
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_the_playout_loop()
    {
        // The loop serves every track on the session. One bad subscriber must not take the room with it.
        var seen = 0;
        await using var buffers = new BundledAudioJitterBuffers(
            [Track(AudioMid)],
            TimeSpan.FromMilliseconds(5),
            initialDelayMs: 20,
            (_, _) =>
            {
                if (Interlocked.Increment(ref seen) == 1)
                {
                    throw new InvalidOperationException("subscriber blew up");
                }
            },
            NullLogger.Instance);

        buffers.TryAdd(AudioMid, Packet(0, 0));
        buffers.TryAdd(AudioMid, Packet(1, 960));

        await WaitUntilAsync(() => Volatile.Read(ref seen) >= 2);

        Assert.True(Volatile.Read(ref seen) >= 2);
    }

    private static int Count<T>(List<T> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }
}
