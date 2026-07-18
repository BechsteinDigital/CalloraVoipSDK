namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Derives a bitrate (bits/second) from successive cumulative byte counts. The first sample only sets the
/// baseline and returns <see langword="null"/>; each later sample returns the rate over the elapsed
/// interval. Not thread-safe — the owner serialises calls.
/// </summary>
internal sealed class BitrateMeter
{
    private long _lastBytes;
    private long _lastTicks;
    private bool _primed;

    /// <summary>
    /// Records <paramref name="totalBytes"/> at <paramref name="nowTicks"/> (100 ns ticks, e.g.
    /// <see cref="DateTime.UtcNow"/>.Ticks) and returns the bitrate since the previous sample, or
    /// <see langword="null"/> on the first sample or a non-positive interval.
    /// </summary>
    public double? Sample(long totalBytes, long nowTicks)
    {
        if (!_primed)
        {
            _primed = true;
            _lastBytes = totalBytes;
            _lastTicks = nowTicks;
            return null;
        }

        var deltaBytes = totalBytes - _lastBytes;
        var deltaTicks = nowTicks - _lastTicks;
        _lastBytes = totalBytes;
        _lastTicks = nowTicks;

        if (deltaTicks <= 0)
        {
            return null;
        }

        var seconds = (double)deltaTicks / TimeSpan.TicksPerSecond;
        return deltaBytes * 8 / seconds;
    }
}
