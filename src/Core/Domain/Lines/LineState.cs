namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>Describes the current registration state of a phone line.</summary>
public enum LineState
{
    /// <summary>Not registered; no active SIP binding.</summary>
    Unregistered,

    /// <summary>A registration request is in flight.</summary>
    Registering,

    /// <summary>Successfully registered with the SIP registrar.</summary>
    Registered,

    /// <summary>
    /// A previous registration was lost and the SDK is waiting to retry.
    /// Transitions back to <see cref="Registering"/> before each attempt.
    /// </summary>
    Reconnecting,

    /// <summary>The most recent registration attempt failed; retry may follow.</summary>
    RegistrationFailed,

    /// <summary>
    /// Re-registration permanently failed — either <see cref="ReregisterOptions.MaxRetries"/>
    /// was exceeded or the server rejected credentials (401/403).
    /// No further attempts will be made.
    /// </summary>
    Failed,

    /// <summary>
    /// Operational without a registration: the line never sends REGISTER because
    /// <see cref="SipAccount.Register"/> is <see langword="false"/> (an IP-authenticated static-IP
    /// trunk, where the peer recognises us by source address). Calls can be placed and received.
    /// </summary>
    /// <remarks>
    /// This is a steady state, not a step towards <see cref="Registered"/> — a line in this mode never
    /// reaches that state, and <see cref="Unregistered"/> would misreport it as unusable. Consumers
    /// gating on "line is ready to dial" must accept <see cref="Registered"/> <b>or</b> this value.
    /// Appended at the end of the enum so the existing members keep their numeric values.
    /// </remarks>
    Ready
}
