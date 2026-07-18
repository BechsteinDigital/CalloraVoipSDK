namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Derives a rate (units/second) from successive cumulative counts — e.g. frames/second from a frame
/// counter. The first sample only sets the baseline and returns <see langword="null"/>; each later sample
/// returns the rate over the elapsed interval. Not thread-safe — the owner serialises calls.
/// </summary>
internal sealed class RateMeter
{
    private long _lastValue;
    private long _lastTicks;
    private bool _primed;

    /// <summary>
    /// Records <paramref name="total"/> at <paramref name="nowTicks"/> (100 ns ticks) and returns the rate
    /// per second since the previous sample, or <see langword="null"/> on the first sample or a
    /// non-positive interval.
    /// </summary>
    public double? Sample(long total, long nowTicks)
    {
        if (!_primed)
        {
            _primed = true;
            _lastValue = total;
            _lastTicks = nowTicks;
            return null;
        }

        var deltaValue = total - _lastValue;
        var deltaTicks = nowTicks - _lastTicks;
        _lastValue = total;
        _lastTicks = nowTicks;

        if (deltaTicks <= 0)
        {
            return null;
        }

        return deltaValue * (double)TimeSpan.TicksPerSecond / deltaTicks;
    }
}
