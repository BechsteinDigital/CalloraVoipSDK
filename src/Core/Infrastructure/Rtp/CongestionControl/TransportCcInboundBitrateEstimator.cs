namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Thread-safe estimator of the inbound (received) bitrate over a short sliding window, used to size the
/// transport-cc feedback interval adaptively (libwebrtc's <c>RemoteEstimatorProxy</c> scales feedback with
/// the observed bitrate so the feedback overhead stays a small, roughly constant fraction of the traffic).
/// <para>
/// The receive path calls <see cref="Observe"/> once per stamped arrival with the bytes it estimates were
/// on the wire; the flush loop calls <see cref="EstimateBitrateBps"/> to read the current rate. Bytes are
/// bucketed by a monotonic clock and buckets older than the window are evicted, so the estimate tracks the
/// recent rate rather than a lifetime average. Nothing is allocated per observation on the hot receive path.
/// </para>
/// </summary>
internal sealed class TransportCcInboundBitrateEstimator
{
    // A ring of fixed-duration buckets covers the window; per-observation cost is O(1) with no allocation.
    // Sub-buckets keep eviction granular so the estimate does not lurch when a whole window's worth of bytes
    // ages out in one step.
    private const int BucketCount = 10;

    private readonly object _sync = new();
    private readonly long[] _bucketBytes = new long[BucketCount];
    private readonly long _windowTicks;
    private readonly long _bucketTicks;
    private readonly long _ticksPerSecond;

    private long _currentBucketIndex; // monotonic bucket ordinal (bucketBytes[_ index % BucketCount])
    private bool _hasData;

    /// <summary>
    /// Creates an estimator averaging over <paramref name="windowTicks"/> of monotonic time.
    /// </summary>
    /// <param name="windowTicks">Sliding-window length, in ticks of the monotonic clock.</param>
    /// <param name="ticksPerSecond">Ticks per second of the monotonic clock (to convert bytes to bits/s).</param>
    /// <exception cref="ArgumentOutOfRangeException">Either argument is not positive.</exception>
    public TransportCcInboundBitrateEstimator(long windowTicks, long ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        _windowTicks = windowTicks;
        _ticksPerSecond = ticksPerSecond;
        _bucketTicks = Math.Max(1, windowTicks / BucketCount);
    }

    /// <summary>
    /// Records <paramref name="bytesOnWire"/> received at monotonic time <paramref name="timestamp"/>. Safe to
    /// call concurrently with <see cref="EstimateBitrateBps"/>. Non-positive byte counts are ignored.
    /// </summary>
    /// <param name="bytesOnWire">Estimated bytes on the wire for this arrival (payload plus header overhead).</param>
    /// <param name="timestamp">A monotonic arrival timestamp, in the same ticks as the constructor's window.</param>
    public void Observe(long bytesOnWire, long timestamp)
    {
        if (bytesOnWire <= 0)
            return;

        var bucket = timestamp / _bucketTicks;
        lock (_sync)
        {
            AdvanceTo(bucket);
            _bucketBytes[(int)(bucket % BucketCount)] += bytesOnWire;
            _hasData = true;
        }
    }

    /// <summary>
    /// The received bitrate in bits per second over the sliding window ending at <paramref name="now"/>, or
    /// <c>null</c> if no bytes have been observed yet (the caller should then fall back to a default interval).
    /// Safe to call concurrently with <see cref="Observe"/>.
    /// </summary>
    /// <param name="now">The current monotonic timestamp, in the same ticks as the constructor's window.</param>
    public double? EstimateBitrateBps(long now)
    {
        lock (_sync)
        {
            if (!_hasData)
                return null;

            AdvanceTo(now / _bucketTicks);

            long total = 0;
            for (var i = 0; i < BucketCount; i++)
                total += _bucketBytes[i];

            if (total <= 0)
                return 0.0;

            // bits over the window, scaled to a per-second rate by the window length.
            return total * 8.0 * _ticksPerSecond / _windowTicks;
        }
    }

    // Rolls the ring forward to the given bucket ordinal, zeroing every bucket that scrolled out of the
    // window since the last observation. Caller holds _sync.
    private void AdvanceTo(long bucket)
    {
        if (bucket <= _currentBucketIndex)
            return; // same or (under reordering) an older bucket — its slot is already live

        var steps = bucket - _currentBucketIndex;
        if (steps >= BucketCount)
        {
            Array.Clear(_bucketBytes); // the whole window aged out
        }
        else
        {
            for (var i = 1; i <= steps; i++)
                _bucketBytes[(int)((_currentBucketIndex + i) % BucketCount)] = 0;
        }

        _currentBucketIndex = bucket;
    }
}
