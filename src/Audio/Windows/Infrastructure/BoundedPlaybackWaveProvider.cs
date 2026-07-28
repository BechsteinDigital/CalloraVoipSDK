using NAudio.Wave;
using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Windows;

/// <summary>
/// An NAudio <see cref="IWaveProvider"/> backed by the shared drop-oldest
/// <see cref="BoundedPlaybackBuffer"/>. NAudio's own <c>BufferedWaveProvider</c> with
/// <c>DiscardOnBufferOverflow</c> drops the <em>newest</em> samples on overflow, the opposite of the
/// jitter-buffer-correct drop-oldest policy the Linux device and <see cref="BoundedPlaybackBuffer"/>
/// implement (issue #18, A3 / HARD-F4). Routing Windows playback through this provider gives both
/// platforms identical overflow behaviour and a real dropped-frame metric. The provider is the
/// single reader of the buffer; the receive path is the writer.
/// </summary>
public sealed class BoundedPlaybackWaveProvider : IWaveProvider
{
    private readonly BoundedPlaybackBuffer _buffer;

    // Carry-over from a decoded frame larger than a single Read request, so oversized frames are not
    // truncated but streamed across successive reads.
    private byte[] _residual = Array.Empty<byte>();
    private int _residualOffset;

    /// <summary>
    /// Creates a wave provider that plays frames drawn from <paramref name="buffer"/> using
    /// <paramref name="waveFormat"/>.
    /// </summary>
    /// <param name="waveFormat">The PCM format the output stream was opened with.</param>
    /// <param name="buffer">The bounded, drop-oldest source of decoded playback frames.</param>
    public BoundedPlaybackWaveProvider(WaveFormat waveFormat, BoundedPlaybackBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);
        ArgumentNullException.ThrowIfNull(buffer);
        WaveFormat = waveFormat;
        _buffer = buffer;
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <inheritdoc />
    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var written = 0;
        while (written < count)
        {
            if (_residualOffset >= _residual.Length)
            {
                if (!_buffer.TryDequeue(out var next) || next is null || next.Length == 0)
                    break;

                _residual = next;
                _residualOffset = 0;
            }

            var available = _residual.Length - _residualOffset;
            var take = Math.Min(available, count - written);
            Buffer.BlockCopy(_residual, _residualOffset, buffer, offset + written, take);
            _residualOffset += take;
            written += take;
        }

        // WaveOutEvent expects the buffer fully filled; pad the tail with silence on underrun so the
        // output stream never stalls waiting for a short read.
        if (written < count)
        {
            Array.Clear(buffer, offset + written, count - written);
            written = count;
        }

        return written;
    }
}
