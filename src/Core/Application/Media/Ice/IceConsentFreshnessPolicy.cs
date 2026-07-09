namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// RFC 7675 consent-freshness timing for a nominated ICE pair: paces the periodic STUN consent
/// checks and decides when consent has expired. Consent is lost when no consent check has been
/// answered within <see cref="ConsentExpiry"/> (30 s, RFC 7675 §5.1); the interval between checks
/// is a base value randomized between 0.8 and 1.2, following the ICE Ta pacing (RFC 8445 §14.3),
/// so peers do not synchronize their traffic. Pure timing logic — the send/receive loop is
/// layered on top (see <see cref="IceConsentMonitor"/>).
/// </summary>
internal sealed class IceConsentFreshnessPolicy
{
    /// <summary>Consent lifetime without an answered check (RFC 7675 §5.1).</summary>
    public static readonly TimeSpan ConsentExpiry = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DefaultBaseInterval = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _baseInterval;

    /// <summary>
    /// Creates a policy with the given base check interval (default 5 s). The interval must be
    /// positive and shorter than <see cref="ConsentExpiry"/> so several checks fit inside the
    /// consent lifetime.
    /// </summary>
    public IceConsentFreshnessPolicy(TimeSpan? baseInterval = null)
    {
        var interval = baseInterval ?? DefaultBaseInterval;
        if (interval <= TimeSpan.Zero || interval >= ConsentExpiry)
            throw new ArgumentOutOfRangeException(
                nameof(baseInterval),
                "Consent check interval must be positive and shorter than the 30 s consent expiry.");
        _baseInterval = interval;
    }

    /// <summary>Base check interval before randomization.</summary>
    public TimeSpan BaseInterval => _baseInterval;

    /// <summary>
    /// Delay until the next consent check: the base interval scaled by a factor in [0.8, 1.2]
    /// (ICE Ta pacing, RFC 8445 §14.3). <paramref name="random01"/> is a value in [0, 1] (clamped).
    /// </summary>
    public TimeSpan NextCheckDelay(double random01)
    {
        var factor = 0.8 + 0.4 * Math.Clamp(random01, 0.0, 1.0);
        return _baseInterval * factor;
    }

    /// <summary>
    /// True while consent is still fresh: the last answered check at <paramref name="lastConfirmed"/>
    /// is within <see cref="ConsentExpiry"/> of <paramref name="now"/>.
    /// </summary>
    public bool IsConsentFresh(DateTimeOffset lastConfirmed, DateTimeOffset now)
        => now - lastConfirmed <= ConsentExpiry;
}
