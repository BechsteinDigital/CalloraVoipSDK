namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Tracks a smoothed packet-loss ratio from transport-cc reports — the loss-based congestion signal,
/// complementary to the delay trend. Each report's lost-to-reported fraction feeds an exponentially
/// weighted moving average, so a transient loss burst does not swing the estimate. Stateful across
/// reports and thread-safe — the feedback loop observes while the application reads the ratio. Loss
/// is exposed as the raw ratio; the threshold at which it should curb the bitrate is a rate-control
/// policy, not decided here.
/// </summary>
internal sealed class TransportCcLossEstimator
{
    private readonly object _sync = new();
    private readonly double _smoothingFactor;
    private double _lossRatio;

    /// <summary>
    /// Creates the estimator.
    /// </summary>
    /// <param name="smoothingFactor">EWMA weight for each new report's loss fraction, in (0, 1].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="smoothingFactor"/> is outside (0, 1].</exception>
    public TransportCcLossEstimator(double smoothingFactor)
    {
        if (smoothingFactor is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(
                nameof(smoothingFactor), smoothingFactor, "Smoothing factor must be in (0, 1].");

        _smoothingFactor = smoothingFactor;
    }

    /// <summary>The current smoothed packet-loss ratio in [0, 1] (fraction of reported packets lost).</summary>
    public double LossRatio
    {
        get { lock (_sync) return _lossRatio; }
    }

    /// <summary>
    /// Folds one report's loss fraction into the estimate. A report with no packets leaves the ratio
    /// unchanged (it carries no loss information).
    /// </summary>
    public void Observe(IReadOnlyList<TransportCcFeedbackResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
            return;

        var received = 0;
        foreach (var result in results)
        {
            if (result.Received)
                received++;
        }

        var ratio = (double)(results.Count - received) / results.Count;
        lock (_sync)
            _lossRatio = _lossRatio * (1 - _smoothingFactor) + ratio * _smoothingFactor;
    }
}
