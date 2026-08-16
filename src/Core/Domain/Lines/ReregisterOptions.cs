namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>
/// Controls automatic re-registration behavior when a SIP registration is lost due to a network
/// disruption or server restart.
/// </summary>
public sealed class ReregisterOptions
{
    /// <summary>
    /// Default options: auto-reregister enabled, unlimited retries, exponential backoff 2 s – 60 s.
    /// </summary>
    public static readonly ReregisterOptions Default = new();

    /// <summary>
    /// Disabled options: no automatic re-registration; line transitions directly to
    /// <see cref="LineState.RegistrationFailed"/> on disruption.
    /// </summary>
    public static readonly ReregisterOptions Disabled = new() { AutoReregister = false };

    /// <summary>
    /// When <see langword="true"/> the SDK automatically attempts to re-register after a
    /// connection disruption. Default: <see langword="true"/>.
    /// </summary>
    public bool AutoReregister { get; init; } = true;

    /// <summary>
    /// Maximum number of reconnect attempts before the line permanently transitions to
    /// <see cref="LineState.Failed"/>.  A value of <c>0</c> means unlimited retries.
    /// Default: <c>0</c>.
    /// </summary>
    public int MaxRetries
    {
        get => _maxRetries;
        init => _maxRetries = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxRetries), value, "Retry count cannot be negative.");
    }

    private readonly int _maxRetries;

    /// <summary>
    /// Delay before the first reconnect attempt and base for exponential backoff.
    /// Default: 2 seconds.
    /// </summary>
    public TimeSpan InitialRetryDelay
    {
        get => _initialRetryDelay;
        init => _initialRetryDelay = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(InitialRetryDelay), value, "The first retry delay must be positive.");
    }

    private readonly TimeSpan _initialRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on the delay between reconnect attempts (caps exponential growth).
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay
    {
        get => _maxRetryDelay;
        init => _maxRetryDelay = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxRetryDelay), value, "The retry delay ceiling must be positive.");
    }

    private readonly TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fraction of the granted registration lifetime at which the binding is refreshed
    /// (0 &lt; ratio &lt; 1). Default: <c>0.8</c> (refresh at 80% of the lifetime).
    /// </summary>
    public double RefreshRatio
    {
        get => _refreshRatio;
        init => _refreshRatio = value is > 0 and < 1
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(RefreshRatio), value, "The refresh ratio must be strictly between 0 and 1.");
    }

    private readonly double _refreshRatio = 0.8;

    /// <summary>
    /// Lower bound on the refresh interval, guarding against REGISTER churn when a registrar
    /// reports an implausibly short lifetime. Never applied above the binding lifetime itself.
    /// Default: 15 seconds.
    /// </summary>
    public TimeSpan MinRefreshInterval
    {
        get => _minRefreshInterval;
        init => _minRefreshInterval = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MinRefreshInterval), value, "The refresh floor must be positive.");
    }

    private readonly TimeSpan _minRefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maximum consecutive corrective re-registrations triggered by a changed NAT-learned
    /// public contact within one cycle, before the line settles on the current address. Caps
    /// a re-register storm on a pathological NAT that reflects a new port on every REGISTER.
    /// Default: <c>3</c>.
    /// </summary>
    public int MaxCorrectiveReregistrations
    {
        get => _maxCorrectiveReregistrations;
        init => _maxCorrectiveReregistrations = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxCorrectiveReregistrations), value, "The corrective re-registration cap cannot be negative.");
    }

    private readonly int _maxCorrectiveReregistrations = 3;

    /// <summary>
    /// Checks the constraints that span two properties, which an individual initialiser cannot see because it
    /// does not know whether the other one has been set yet (#165 P3-11). Called once when the line that owns
    /// these options is built, so a contradictory backoff window is rejected before any REGISTER goes out
    /// rather than surfacing as odd retry timing much later.
    /// </summary>
    /// <exception cref="ArgumentException">The retry ceiling is below the first retry delay.</exception>
    internal void Validate()
    {
        if (MaxRetryDelay < InitialRetryDelay)
        {
            throw new ArgumentException(
                $"MaxRetryDelay ({MaxRetryDelay}) must be at least InitialRetryDelay ({InitialRetryDelay}); " +
                "the backoff would have no room to grow.", nameof(MaxRetryDelay));
        }
    }
}
