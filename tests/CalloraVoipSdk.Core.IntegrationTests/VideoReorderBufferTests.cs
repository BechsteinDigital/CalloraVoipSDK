using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video reorder buffer (WebRTC video playout, RFC 3550 ordering + RFC 4588 RTX window): a
/// contiguous-release window that emits video RTP packets in ascending sequence order, passing
/// an in-order stream straight through (no buffering latency), holding only behind a gap to let
/// a reordered/retransmitted packet slot in, skipping a gap it outgrows, and dropping
/// duplicate/too-late packets.
/// </summary>
public sealed class VideoReorderBufferTests
{
    [Fact]
    public void In_order_stream_releases_immediately_without_holding()
    {
        var buffer = new VideoReorderBuffer(depth: 8);

        // Every in-order packet comes straight back out; nothing is ever held.
        for (ushort seq = 1; seq <= 6; seq++)
        {
            Assert.Equal((ushort[])[seq], SequencesOf(buffer.Insert(Packet(seq))).ToArray());
            Assert.Equal(0, buffer.BufferedCount);
        }
    }

    [Fact]
    public void In_order_inserts_pass_through_in_order()
    {
        var buffer = new VideoReorderBuffer(depth: 3);
        var released = new List<ushort>();

        for (ushort seq = 1; seq <= 6; seq++)
            released.AddRange(SequencesOf(buffer.Insert(Packet(seq))));
        released.AddRange(SequencesOf(buffer.Flush()));

        Assert.Equal((ushort[])[1, 2, 3, 4, 5, 6], released.ToArray());
    }

    [Fact]
    public void A_gap_holds_subsequent_packets_until_it_is_filled()
    {
        var buffer = new VideoReorderBuffer(depth: 8);

        Assert.Equal((ushort[])[1], SequencesOf(buffer.Insert(Packet(1))).ToArray());
        Assert.Empty(buffer.Insert(Packet(3))); // gap at 2 → held
        Assert.Empty(buffer.Insert(Packet(4))); // still held behind the gap
        Assert.Equal(2, buffer.BufferedCount);

        // The missing 2 arrives (reorder or RTX): it and the contiguous run behind it release.
        Assert.Equal((ushort[])[2, 3, 4], SequencesOf(buffer.Insert(Packet(2))).ToArray());
        Assert.Equal(0, buffer.BufferedCount);
    }

    [Fact]
    public void Reordered_pairs_are_corrected_and_released_in_order()
    {
        var buffer = new VideoReorderBuffer(depth: 8);
        var released = new List<ushort>();

        // Adjacent swaps (none arriving before the baseline) are corrected as each gap fills.
        foreach (var seq in (ushort[])[1, 3, 2, 5, 4, 7, 6, 8])
            released.AddRange(SequencesOf(buffer.Insert(Packet(seq))));

        Assert.Equal((ushort[])[1, 2, 3, 4, 5, 6, 7, 8], released.ToArray());
        Assert.Equal(0, buffer.BufferedCount);
    }

    [Fact]
    public void Late_reordered_packet_slots_in_before_it_is_released()
    {
        var buffer = new VideoReorderBuffer(depth: 4);
        var released = new List<ushort>();

        // 3 arrives after 4/5 but before the window slides past it.
        foreach (var seq in (ushort[])[1, 2, 4, 5, 3, 6, 7, 8])
            released.AddRange(SequencesOf(buffer.Insert(Packet(seq))));
        released.AddRange(SequencesOf(buffer.Flush()));

        Assert.Equal((ushort[])[1, 2, 3, 4, 5, 6, 7, 8], released.ToArray());
    }

    [Fact]
    public void A_gap_the_window_outgrows_is_skipped()
    {
        var buffer = new VideoReorderBuffer(depth: 2);
        var released = new List<ushort>();

        // 4 is never delivered; once more than depth packets pile up behind it the window skips.
        foreach (var seq in (ushort[])[1, 2, 3, 5, 6, 7])
            released.AddRange(SequencesOf(buffer.Insert(Packet(seq))));
        released.AddRange(SequencesOf(buffer.Flush()));

        Assert.Equal((ushort[])[1, 2, 3, 5, 6, 7], released.ToArray());
        Assert.DoesNotContain((ushort)4, released);
    }

    [Fact]
    public void Duplicate_held_behind_a_gap_is_dropped()
    {
        var buffer = new VideoReorderBuffer(depth: 4);

        Assert.Equal((ushort[])[1], SequencesOf(buffer.Insert(Packet(1))).ToArray());
        Assert.Empty(buffer.Insert(Packet(3))); // held behind the gap at 2
        Assert.Empty(buffer.Insert(Packet(3))); // duplicate of the held packet
        Assert.Equal(1, buffer.BufferedCount);
    }

    [Fact]
    public void Too_late_packet_below_the_expected_mark_is_dropped()
    {
        var buffer = new VideoReorderBuffer(depth: 4);

        // In-order 1,2,3 all release; the next expected is now 4.
        for (ushort seq = 1; seq <= 3; seq++)
            Assert.Equal((ushort[])[seq], SequencesOf(buffer.Insert(Packet(seq))).ToArray());

        // A stale retransmit of 2 arrives late → already released past → dropped, nothing held.
        Assert.Empty(buffer.Insert(Packet(2)));
        Assert.Equal(0, buffer.BufferedCount);
    }

    [Fact]
    public void Sequence_wrap_is_ordered_across_the_boundary()
    {
        var buffer = new VideoReorderBuffer(depth: 3);
        var released = new List<ushort>();

        foreach (var seq in (ushort[])[65534, 65535, 0, 1, 2])
            released.AddRange(SequencesOf(buffer.Insert(Packet(seq))));
        released.AddRange(SequencesOf(buffer.Flush()));

        Assert.Equal((ushort[])[65534, 65535, 0, 1, 2], released.ToArray());
    }

    [Fact]
    public void Depth_of_one_holds_a_single_packet_then_skips_the_gap()
    {
        var buffer = new VideoReorderBuffer(depth: 1);
        var released = new List<ushort>();

        released.AddRange(SequencesOf(buffer.Insert(Packet(1)))); // releases 1
        Assert.Empty(buffer.Insert(Packet(3)));                   // holds one behind the gap at 2
        Assert.Equal(1, buffer.BufferedCount);
        released.AddRange(SequencesOf(buffer.Insert(Packet(4)))); // over depth → skip 2, release 3,4

        Assert.Equal((ushort[])[1, 3, 4], released.ToArray());
        Assert.DoesNotContain((ushort)2, released);
    }

    [Fact]
    public void Flush_on_empty_buffer_returns_nothing()
    {
        var buffer = new VideoReorderBuffer(depth: 4);
        Assert.Empty(buffer.Flush());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16385)]
    public void Depth_out_of_range_is_rejected(int depth)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new VideoReorderBuffer(depth));

    private static RtpPacket Packet(ushort sequenceNumber) => new()
    {
        PayloadType = 96,
        SequenceNumber = sequenceNumber,
        Timestamp = sequenceNumber * 3000u,
        Ssrc = 0x1234,
        Payload = new byte[] { (byte)(sequenceNumber & 0xFF) },
    };

    private static IEnumerable<ushort> SequencesOf(IReadOnlyList<RtpPacket> packets)
    {
        foreach (var packet in packets)
            yield return packet.SequenceNumber;
    }
}
