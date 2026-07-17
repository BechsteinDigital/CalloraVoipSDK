using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk;

/// <summary>
/// Policy facade exposing active SDK policy defaults.
/// </summary>
public sealed class PolicyManager : IPolicyManager
{
    internal PolicyManager(SrtpPolicy defaultSrtpPolicy)
    {
        DefaultSrtpPolicy = defaultSrtpPolicy;
    }

    /// <summary>
    /// Global SRTP policy configured for the SDK client.
    /// </summary>
    public SrtpPolicy DefaultSrtpPolicy { get; }

    /// <summary>
    /// Resolves the effective SRTP policy after optional per-call override.
    /// </summary>
    public SrtpPolicy ResolveEffectiveSrtpPolicy(bool? useSrtpOverride)
    {
        if (useSrtpOverride is null)
            return DefaultSrtpPolicy;

        return useSrtpOverride.Value ? SrtpPolicy.Required : SrtpPolicy.Disabled;
    }
}
