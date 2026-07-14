using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video reorder buffer (WebRTC video playout, RFC 3550 ordering + RFC 4588 RTX window): a
/// depth-bounded window that emits video RTP packets in ascending sequence order, correcting
/// in-window reordering, skipping gaps it outgrows, and dropping duplicate/too-late packets.
/// </summary>
public sealed class VideoReorderBufferTests
{
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
    public void Reordered_inserts_within_depth_emerge_sorted()
    {
        var buffer = new VideoReorderBuffer(depth: 8);

        // A fully shuffled burst that fits inside the window is reordered on flush.
        foreach (var seq in (ushort[])[5, 2, 8, 1, 7, 3, 6, 4])
            Assert.Empty(buffer.Insert(Packet(seq))); // window still filling → nothing released

        var released = SequencesOf(buffer.Flush());

        Assert.Equal((ushort[])[1, 2, 3, 4, 5, 6, 7, 8], released.ToArray());
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

        // 4 is never delivered; the window can only hold 2, so it slides past the gap.
        foreach (var seq in (ushort[])[1, 2, 3, 5, 6, 7])
            released.AddRange(SequencesOf(buffer.Insert(Packet(seq))));
        released.AddRange(SequencesOf(buffer.Flush()));

        Assert.Equal((ushort[])[1, 2, 3, 5, 6, 7], released.ToArray());
        Assert.DoesNotContain((ushort)4, released);
    }

    [Fact]
    public void Duplicate_still_in_the_window_is_dropped()
    {
        var buffer = new VideoReorderBuffer(depth: 4);

        Assert.Empty(buffer.Insert(Packet(5)));
        Assert.Empty(buffer.Insert(Packet(5))); // duplicate
        Assert.Equal(1, buffer.BufferedCount);
    }

    [Fact]
    public void Too_late_packet_below_the_released_mark_is_dropped()
    {
        var buffer = new VideoReorderBuffer(depth: 2);

        // Fill and release 1 (window holds 2, 3).
        Assert.Empty(buffer.Insert(Packet(1)));
        Assert.Empty(buffer.Insert(Packet(2)));
        Assert.Equal((ushort[])[1], SequencesOf(buffer.Insert(Packet(3))).ToArray());

        // 1 arrives again, already released past → dropped, window unchanged.
        Assert.Empty(buffer.Insert(Packet(1)));
        Assert.Equal(2, buffer.BufferedCount);
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
    public void Depth_of_one_releases_every_packet_but_the_newest()
    {
        var buffer = new VideoReorderBuffer(depth: 1);
        var released = new List<ushort>();

        // With a one-packet window, each new in-order packet releases its predecessor.
        released.AddRange(SequencesOf(buffer.Insert(Packet(1)))); // holds 1
        released.AddRange(SequencesOf(buffer.Insert(Packet(2)))); // releases 1, holds 2
        released.AddRange(SequencesOf(buffer.Insert(Packet(3)))); // releases 2, holds 3

        Assert.Equal((ushort[])[1, 2], released.ToArray());
        Assert.Equal(1, buffer.BufferedCount);
        Assert.Equal((ushort[])[3], SequencesOf(buffer.Flush()).ToArray());
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
