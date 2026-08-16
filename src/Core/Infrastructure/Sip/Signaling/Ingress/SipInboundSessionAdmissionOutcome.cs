namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Why an inbound session was or was not admitted (#279). Both refusals answer 486 Busy Here; they are kept
/// apart so the log names the ceiling that was actually hit.
/// </summary>
internal enum SipInboundSessionAdmissionOutcome
{
    /// <summary>A slot was reserved; the caller must release it unless the session ends up tracked.</summary>
    Admitted,

    /// <summary>The global concurrent-inbound-session ceiling is exhausted.</summary>
    GlobalCapReached,

    /// <summary>The source address already holds its share of the global budget.</summary>
    PerRemoteCapReached
}
