using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// The bounded, drop-oldest playback buffer that caps the audio device's decoded-PCM queue
/// (HARD-F4). It must never buffer more than its capacity, must drop the stalest frames (not the
/// freshest) on overflow, must count those drops as a backpressure metric, and must reset cleanly.
/// </summary>
public sealed class BoundedPlaybackBufferTests
{
    private static byte[] Frame(byte tag) => [tag];

    [Fact]
    public void Enqueue_beyond_capacity_bounds_depth_and_drops_the_oldest_frames()
    {
        const int capacity = 4;
        var buffer = new BoundedPlaybackBuffer(capacity);

        // Push twice the capacity: frames 0..7. The buffer must keep only the freshest four (4..7)
        // and report four dropped, with depth pinned at the capacity — not growing to eight.
        for (byte i = 0; i < 8; i++)
            buffer.Enqueue(Frame(i));

        Assert.Equal(capacity, buffer.Depth);
        Assert.Equal(4, buffer.DroppedFrames);

        var drained = new List<byte>();
        while (buffer.TryDequeue(out var frame))
            drained.Add(frame![0]);

        // Drop-oldest: the stalest frames (0..3) were discarded, the freshest (4..7) survived in order.
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, drained);
    }

    [Fact]
    public void Enqueue_within_capacity_drops_nothing_and_preserves_order()
    {
        var buffer = new BoundedPlaybackBuffer(8);

        for (byte i = 0; i < 5; i++)
            buffer.Enqueue(Frame(i));

        Assert.Equal(5, buffer.Depth);
        Assert.Equal(0, buffer.DroppedFrames);

        for (byte i = 0; i < 5; i++)
        {
            Assert.True(buffer.TryDequeue(out var frame));
            Assert.Equal(i, frame![0]);
        }
    }

    [Fact]
    public void TryDequeue_on_empty_buffer_returns_false_and_null()
    {
        var buffer = new BoundedPlaybackBuffer(4);

        Assert.False(buffer.TryDequeue(out var frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Clear_discards_buffered_frames_and_resets_the_drop_metric()
    {
        var buffer = new BoundedPlaybackBuffer(2);
        for (byte i = 0; i < 6; i++)
            buffer.Enqueue(Frame(i));

        Assert.Equal(2, buffer.Depth);
        Assert.True(buffer.DroppedFrames > 0);

        buffer.Clear();

        Assert.Equal(0, buffer.Depth);
        Assert.Equal(0, buffer.DroppedFrames);
        Assert.False(buffer.TryDequeue(out _));
    }

    [Fact]
    public void Enqueue_null_frame_throws()
    {
        var buffer = new BoundedPlaybackBuffer(4);
        Assert.Throws<ArgumentNullException>(() => buffer.Enqueue(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_capacity_throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedPlaybackBuffer(capacity));
    }
}
