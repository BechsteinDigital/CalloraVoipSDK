namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Turns the congestion signal (delay trend + loss ratio) into a recommended target video bitrate
/// with a simple AIMD rule: multiplicatively back off when the delay is overusing or loss crosses the
/// threshold, additively probe upward when the network looks healthy, clamped to a configured range.
/// This is the policy the public API exposes as a ready-to-use recommendation — the application sets
/// its encoder to it; the SDK never encodes (transport-only). Stateful and thread-safe: the feedback
/// loop updates it while the application reads the recommendation.
/// </summary>
/// <remarks>
/// A deliberately simple AIMD, not GCC's or SCReAM's rate control — a smoother, standards-based
/// controller (RFC 8298) is a later upgrade. Detection quality lives in the estimators; this only
/// maps a signal to a bitrate.
/// </remarks>
internal sealed class CongestionBitrateController
{
    private readonly long _minBitrateBps;
    private readonly long _maxBitrateBps;
    private readonly long _increaseStepBps;
    private readonly double _decreaseFactor;
    private readonly double _lossThreshold;
    private readonly object _sync = new();
    private long _targetBitrateBps;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="initialBitrateBps">Starting target bitrate; must be within [min, max].</param>
    /// <param name="minBitrateBps">Lower clamp (bits per second), positive.</param>
    /// <param name="maxBitrateBps">Upper clamp (bits per second), ≥ min.</param>
    /// <param name="increaseStepBps">Additive probe step per healthy update, positive.</param>
    /// <param name="decreaseFactor">Multiplicative back-off factor on congestion, in (0, 1).</param>
    /// <param name="lossThreshold">Loss ratio [0, 1] at or above which the bitrate backs off regardless of delay.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its valid range.</exception>
    public CongestionBitrateController(
        long initialBitrateBps,
        long minBitrateBps,
        long maxBitrateBps,
        long increaseStepBps,
        double decreaseFactor,
        double lossThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minBitrateBps);
        if (maxBitrateBps < minBitrateBps)
            throw new ArgumentOutOfRangeException(nameof(maxBitrateBps), maxBitrateBps, "Max must be ≥ min.");
        if (initialBitrateBps < minBitrateBps || initialBitrateBps > maxBitrateBps)
            throw new ArgumentOutOfRangeException(
                nameof(initialBitrateBps), initialBitrateBps, "Initial bitrate must be within [min, max].");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(increaseStepBps);
        if (decreaseFactor is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(decreaseFactor), decreaseFactor, "Must be in (0, 1).");
        if (lossThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(lossThreshold), lossThreshold, "Must be in [0, 1].");

        _targetBitrateBps = initialBitrateBps;
        _minBitrateBps = minBitrateBps;
        _maxBitrateBps = maxBitrateBps;
        _increaseStepBps = increaseStepBps;
        _decreaseFactor = decreaseFactor;
        _lossThreshold = lossThreshold;
    }

    /// <summary>The current recommended target video bitrate in bits per second (within [min, max]).</summary>
    public long TargetBitrateBps
    {
        get { lock (_sync) return _targetBitrateBps; }
    }

    /// <summary>
    /// Adjusts the target bitrate for one congestion observation: back off multiplicatively when the
    /// delay is <see cref="CongestionSignal.Overusing"/> or <paramref name="lossRatio"/> is at/above
    /// the threshold, otherwise probe upward additively. The result stays within [min, max].
    /// </summary>
    public void Update(CongestionSignal signal, double lossRatio)
    {
        lock (_sync)
        {
            if (signal == CongestionSignal.Overusing || lossRatio >= _lossThreshold)
                _targetBitrateBps = Math.Max(_minBitrateBps, (long)(_targetBitrateBps * _decreaseFactor));
            else
                _targetBitrateBps = Math.Min(_maxBitrateBps, _targetBitrateBps + _increaseStepBps);
        }
    }
}
