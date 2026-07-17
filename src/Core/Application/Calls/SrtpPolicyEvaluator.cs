using CalloraVoipSdk.Core.Domain.Security;

namespace CalloraVoipSdk.Core.Application.Calls;

/// <summary>
/// Application-layer evaluator for SRTP policy resolution and negotiation outcomes.
/// Infrastructure adapters consume this evaluator but must not own policy rules.
/// </summary>
internal static class SrtpPolicyEvaluator
{
    /// <summary>
    /// Resolves effective policy from global SDK configuration and optional per-call dial override.
    /// </summary>
    public static ResolvedSrtpPolicy ResolveEffectivePolicy(
        SrtpPolicy globalPolicy,
        bool? useSrtpOverride)
    {
        if (useSrtpOverride is null)
            return new ResolvedSrtpPolicy(globalPolicy, Source: "global");

        return useSrtpOverride.Value
            ? new ResolvedSrtpPolicy(SrtpPolicy.Required, Source: "dial_override_true")
            : new ResolvedSrtpPolicy(SrtpPolicy.Disabled, Source: "dial_override_false");
    }

    /// <summary>
    /// Returns true when negotiated media violates the applied SRTP policy.
    /// </summary>
    public static bool IsPolicyViolation(SrtpPolicy policy, bool isSrtpNegotiated) =>
        policy switch
        {
            SrtpPolicy.Required => !isSrtpNegotiated,
            SrtpPolicy.Disabled => isSrtpNegotiated,
            _ => false
        };

    /// <summary>
    /// Maps policy + negotiation result to a stable reason code.
    /// </summary>
    public static string ResolveReasonCode(SrtpPolicy policy, bool isSrtpNegotiated) =>
        policy switch
        {
            SrtpPolicy.Required => isSrtpNegotiated
                ? SrtpDecisionReasonCodes.Negotiated
                : SrtpDecisionReasonCodes.RequiredRemoteNoSrtp,
            SrtpPolicy.Disabled => isSrtpNegotiated
                ? SrtpDecisionReasonCodes.DisabledPolicyRejectedSrtp
                : SrtpDecisionReasonCodes.DisabledByPolicy,
            _ => isSrtpNegotiated
                ? SrtpDecisionReasonCodes.Negotiated
                : SrtpDecisionReasonCodes.OptionalFallbackToRtp
        };
}

/// <summary>
/// Effective SRTP policy and its resolution source.
/// </summary>
internal readonly record struct ResolvedSrtpPolicy(
    SrtpPolicy Policy,
    string Source);
