namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Turns the stream of per-packet delay gradients (see <see cref="TransportCcFeedbackCorrelator"/>)
/// into a smoothed one-way delay trend and a coarse <see cref="CongestionSignal"/>. Each gradient
/// feeds an exponentially weighted moving average; when the smoothed trend rises above the overuse
/// threshold the queue is building (<see cref="CongestionSignal.Overusing"/>), when it falls below
/// the negative threshold the queue is draining. Stateful across reports and thread-safe — the
/// feedback loop observes while the application reads the signal.
/// </summary>
/// <remarks>
/// This uses a fixed threshold and an EWMA rather than libwebrtc's adaptive threshold over a
/// linear-regression trendline; that (or SCReAM, RFC 8298) is a later accuracy upgrade. It detects
/// the trend only — mapping it to a target bitrate is a separate rate-control policy.
/// </remarks>
internal sealed class TransportCcDelayTrendEstimator
{
    private readonly object _sync = new();
    private readonly double _smoothingFactor;
    private readonly long _overuseThresholdMicros;
    private double _trendMicros;

    /// <summary>
    /// Creates the estimator.
    /// </summary>
    /// <param name="smoothingFactor">
    /// EWMA weight for each new gradient, in (0, 1]. Smaller reacts slower but is steadier.
    /// </param>
    /// <param name="overuseThresholdMicros">
    /// Magnitude the smoothed trend must exceed (in microseconds) to signal overuse/underuse. Positive.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its valid range.</exception>
    public TransportCcDelayTrendEstimator(double smoothingFactor, long overuseThresholdMicros)
    {
        if (smoothingFactor is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(
                nameof(smoothingFactor), smoothingFactor, "Smoothing factor must be in (0, 1].");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(overuseThresholdMicros);

        _smoothingFactor = smoothingFactor;
        _overuseThresholdMicros = overuseThresholdMicros;
    }

    /// <summary>The current smoothed one-way delay trend in microseconds (positive = delay rising).</summary>
    public double TrendMicros
    {
        get { lock (_sync) return _trendMicros; }
    }

    /// <summary>The current coarse congestion signal derived from <see cref="TrendMicros"/>.</summary>
    public CongestionSignal Signal
    {
        get
        {
            lock (_sync)
            {
                if (_trendMicros > _overuseThresholdMicros)
                    return CongestionSignal.Overusing;
                if (_trendMicros < -_overuseThresholdMicros)
                    return CongestionSignal.Underusing;
                return CongestionSignal.Normal;
            }
        }
    }

    /// <summary>
    /// Folds one report's delay-gradient samples into the trend, in order. Empty input leaves the
    /// trend unchanged.
    /// </summary>
    public void Observe(IReadOnlyList<TransportCcDelaySample> delaySamples)
    {
        ArgumentNullException.ThrowIfNull(delaySamples);

        lock (_sync)
        {
            foreach (var sample in delaySamples)
                _trendMicros = _trendMicros * (1 - _smoothingFactor) + sample.DelayGradientMicros * _smoothingFactor;
        }
    }
}
