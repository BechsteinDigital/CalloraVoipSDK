using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// A bounded, drop-oldest buffer for decoded PCM playback frames, shared by the platform audio
/// devices. The receive path (network-paced) can burst ahead of the hardware playback callback
/// (device-paced, one frame per invocation); an unbounded queue would grow without limit under
/// jitter or a stalled output stream, inflating both memory and mouth-to-ear latency (HARD-F4).
/// On overflow the stalest frame is discarded — the jitter-buffer-correct policy, keeping playback
/// fresh and latency bounded — and counted so callers can surface backpressure as a metric.
/// Safe for a single reader (the playback callback) concurrent with one or more writers.
/// </summary>
public sealed class BoundedPlaybackBuffer
{
    private readonly Channel<byte[]> _channel;
    private long _droppedFrames;

    /// <summary>
    /// Creates a playback buffer bounded to <paramref name="capacity"/> frames.
    /// </summary>
    /// <param name="capacity">Maximum number of buffered frames; must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is zero or negative.</exception>
    public BoundedPlaybackBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            },
            _ => Interlocked.Increment(ref _droppedFrames));
    }

    /// <summary>
    /// Current number of frames buffered for playback.
    /// </summary>
    public int Depth => _channel.Reader.Count;

    /// <summary>
    /// Cumulative frames dropped by the drop-oldest policy since construction or the last
    /// <see cref="Clear"/>. A non-zero value indicates the receive path is outpacing playback.
    /// </summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>
    /// Buffers <paramref name="frame"/> for playback, evicting and counting the stalest frame when
    /// the buffer is full. The caller hands ownership of the array to the buffer.
    /// </summary>
    /// <param name="frame">The decoded PCM frame to buffer.</param>
    public void Enqueue(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // DropOldest makes TryWrite always succeed; a full buffer evicts the stalest frame (counted
        // via the drop callback) instead of growing without limit.
        _channel.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Removes and returns the next frame for playback.
    /// </summary>
    /// <param name="frame">The dequeued frame, or <see langword="null"/> when the buffer is empty.</param>
    /// <returns><see langword="true"/> when a frame was returned; otherwise <see langword="false"/>.</returns>
    public bool TryDequeue([MaybeNullWhen(false)] out byte[] frame) => _channel.Reader.TryRead(out frame);

    /// <summary>
    /// Discards every buffered frame and resets <see cref="DroppedFrames"/> — used when the device
    /// stops, so the next connection starts with an empty buffer and a fresh drop count.
    /// </summary>
    public void Clear()
    {
        while (_channel.Reader.TryRead(out _))
        {
        }

        Interlocked.Exchange(ref _droppedFrames, 0);
    }
}
