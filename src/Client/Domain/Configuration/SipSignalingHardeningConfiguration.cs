namespace CalloraVoipSdk;

/// <summary>
/// Resource limits for the SIP signaling layer that bound the state a peer can pin on the UAS: how many
/// concurrent inbound dialog sessions may exist (globally and per source IP), how long an un-answered inbound
/// call may ring before it is auto-rejected, and how the inbound server-transaction table is bounded (#158
/// P1-5/P1-7). Supplied via <see cref="VoipConfiguration.SipSignalingHardening"/>; the defaults match the SDK's
/// built-in limits.
/// </summary>
public sealed class SipSignalingHardeningConfiguration
{
    /// <summary>
    /// Maximum concurrent inbound dialog sessions across all remotes. A UAS creates session state for every
    /// served-user INVITE before any line/trunk takes ownership; beyond this cap an inbound INVITE is answered
    /// 486 Busy Here and no session is created. Default 256.
    /// </summary>
    public int MaxConcurrentInboundSessions { get; init; } = 256;

    /// <summary>
    /// Maximum concurrent inbound dialog sessions from a single source IP, fair-sharing the global budget so one
    /// remote cannot occupy every slot. Beyond this per-remote cap an inbound INVITE is answered 486 Busy Here.
    /// Default 32.
    /// </summary>
    public int MaxInboundSessionsPerRemote { get; init; } = 32;

    /// <summary>
    /// Maximum time an inbound call may remain ringing (un-answered) before the SDK auto-rejects it with
    /// 480 Temporarily Unavailable and releases the session, so a peer that never completes the call cannot pin
    /// dialog state indefinitely. Default 180 seconds.
    /// </summary>
    public TimeSpan InboundRingDeadline { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Ceiling on concurrently tracked inbound server transactions. A brand-new transaction beyond this cap is
    /// dropped; existing transactions (retransmissions, in-flight responses) are never refused. Default 8192.
    /// </summary>
    public int MaxServerTransactions { get; init; } = 8192;

    /// <summary>
    /// Absolute lifetime after which an inbound server transaction is reaped regardless of state, so a
    /// transaction that never reaches a final response (e.g. an INVITE answered only with 100 Trying) cannot
    /// linger forever. Chosen above the longest legitimate transaction lifetime. Default 300 seconds.
    /// </summary>
    public TimeSpan AbsoluteServerTransactionLifetime { get; init; } = TimeSpan.FromSeconds(300);
}
