using NAudio.Wave;
using CalloraVoipSdk.Audio.Abstractions.Processing;
using CalloraVoipSdk.Audio.Windows;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// The Windows playback wave provider (issue #18, A3). It must drain the shared drop-oldest buffer
/// (not NAudio's drop-newest BufferedWaveProvider), stream frames larger than one read across
/// successive reads, and pad the tail with silence on underrun so the output stream never stalls.
/// </summary>
public sealed class BoundedPlaybackWaveProviderTests
{
    private static readonly WaveFormat Format = new(8000, 16, 1);

    [Fact]
    public void Reads_buffered_frames_in_order()
    {
        var buffer = new BoundedPlaybackBuffer(8);
        buffer.Enqueue(new byte[] { 1, 2 });
        buffer.Enqueue(new byte[] { 3, 4 });
        var provider = new BoundedPlaybackWaveProvider(Format, buffer);

        var dst = new byte[4];
        var read = provider.Read(dst, 0, 4);

        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, dst);
    }

    [Fact]
    public void Pads_the_tail_with_silence_on_underrun_and_still_reports_a_full_read()
    {
        var buffer = new BoundedPlaybackBuffer(8);
        buffer.Enqueue(new byte[] { 9, 9 });
        var provider = new BoundedPlaybackWaveProvider(Format, buffer);

        var dst = new byte[6];
        Array.Fill(dst, (byte)0x7F);
        var read = provider.Read(dst, 0, 6);

        // WaveOutEvent needs the buffer fully filled; the two real bytes then silence.
        Assert.Equal(6, read);
        Assert.Equal(new byte[] { 9, 9, 0, 0, 0, 0 }, dst);
    }

    [Fact]
    public void Streams_a_frame_larger_than_a_single_read_across_reads()
    {
        var buffer = new BoundedPlaybackBuffer(8);
        buffer.Enqueue(new byte[] { 1, 2, 3, 4 });
        var provider = new BoundedPlaybackWaveProvider(Format, buffer);

        var first = new byte[2];
        Assert.Equal(2, provider.Read(first, 0, 2));
        Assert.Equal(new byte[] { 1, 2 }, first);

        var second = new byte[2];
        Assert.Equal(2, provider.Read(second, 0, 2));
        Assert.Equal(new byte[] { 3, 4 }, second);
    }

    [Fact]
    public void Overflow_drops_the_oldest_frame_not_the_newest()
    {
        var buffer = new BoundedPlaybackBuffer(2);
        buffer.Enqueue(new byte[] { 1, 1 });
        buffer.Enqueue(new byte[] { 2, 2 });
        buffer.Enqueue(new byte[] { 3, 3 }); // evicts {1,1}, the stalest
        var provider = new BoundedPlaybackWaveProvider(Format, buffer);

        var dst = new byte[4];
        provider.Read(dst, 0, 4);

        // Freshest two frames survive in order; the oldest was dropped.
        Assert.Equal(new byte[] { 2, 2, 3, 3 }, dst);
        Assert.Equal(1, buffer.DroppedFrames);
    }

    [Fact]
    public void Empty_buffer_reads_full_silence()
    {
        var provider = new BoundedPlaybackWaveProvider(Format, new BoundedPlaybackBuffer(4));

        var dst = new byte[4];
        Array.Fill(dst, (byte)0x55);
        var read = provider.Read(dst, 0, 4);

        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, dst);
    }
}
