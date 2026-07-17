using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk;

/// <summary>
/// Policy facade exposing active SDK policy defaults.
/// </summary>
public interface IPolicyManager
{
    /// <summary>
    /// Global SRTP policy configured for the SDK client.
    /// </summary>
    SrtpPolicy DefaultSrtpPolicy { get; }

    /// <summary>
    /// Resolves the effective SRTP policy after optional per-call override.
    /// </summary>
    SrtpPolicy ResolveEffectiveSrtpPolicy(bool? useSrtpOverride);
}
