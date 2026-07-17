namespace CalloraVoipSdk.Core.Domain.Security;

/// <summary>
/// Stable reason-code constants for SRTP policy and negotiation decisions.
/// These values are emitted in call media metadata and SIP telemetry.
/// </summary>
public static class SrtpDecisionReasonCodes
{
    /// <summary>No SRTP decision has been evaluated yet.</summary>
    public const string NotEvaluated = "srtp.not_evaluated";

    /// <summary>SRTP was successfully negotiated and accepted by policy.</summary>
    public const string Negotiated = "srtp.negotiated";

    /// <summary>
    /// SRTP policy is optional and remote negotiation ended on plain RTP.
    /// This is an explicit and allowed fallback.
    /// </summary>
    public const string OptionalFallbackToRtp = "srtp.optional.fallback_rtp";

    /// <summary>
    /// SRTP policy is required but no SRTP-capable remote offer/answer is available.
    /// </summary>
    public const string RequiredRemoteNoSrtp = "srtp.required.remote_no_srtp";

    /// <summary>
    /// SRTP policy is required but media negotiation failed under SRTP constraints.
    /// </summary>
    public const string RequiredNegotiationFailed = "srtp.required.negotiation_failed";

    /// <summary>
    /// SRTP policy is disabled; plain RTP was used as configured.
    /// </summary>
    public const string DisabledByPolicy = "srtp.disabled.by_policy";

    /// <summary>
    /// SRTP policy is disabled but the negotiated SDP indicates secure RTP.
    /// This is a policy violation and must fail.
    /// </summary>
    public const string DisabledPolicyRejectedSrtp = "srtp.disabled.rejected_remote_srtp";

    /// <summary>
    /// Remote SDP could not be parsed into media parameters.
    /// </summary>
    public const string MediaParametersUnavailable = "srtp.media_parameters_unavailable";
}
