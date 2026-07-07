namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>
/// Controls how a SIP registration is kept alive: proactive re-registration before the negotiated
/// expiry, and automatic recovery when a registration is lost due to a network disruption or server
/// restart.
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
    /// When <see langword="true"/> the SDK keeps the SIP binding alive automatically: it proactively
    /// re-registers before the registrar-confirmed expiry lapses and retries after a connection
    /// disruption. When <see langword="false"/> the line registers exactly once and performs no
    /// proactive refresh and no automatic reconnect; the binding is expected to be renewed manually.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool AutoReregister { get; init; } = true;

    /// <summary>
    /// Maximum number of reconnect attempts before the line permanently transitions to
    /// <see cref="LineState.Failed"/>.  A value of <c>0</c> means unlimited retries.
    /// Default: <c>0</c>.
    /// </summary>
    public int MaxRetries { get; init; } = 0;

    /// <summary>
    /// Delay before the first reconnect attempt and base for exponential backoff.
    /// Default: 2 seconds.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on the delay between reconnect attempts (caps exponential growth).
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);
}
