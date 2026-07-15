using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Event payload for <see cref="IVideoSender.RecommendedBitrateChanged"/>: the SDK's ready-to-use
/// outbound video bitrate recommendation and the coarse network quality behind it. Set your encoder's
/// target bitrate to <see cref="RecommendedBitrateBps"/> — the SDK never encodes (transport-only).
/// </summary>
public sealed class VideoBitrateRecommendationEventArgs : EventArgs
{
    /// <summary>
    /// Recommended outbound video bitrate in bits per second, or <see langword="null"/> when
    /// congestion control is inactive for this leg.
    /// </summary>
    public long? RecommendedBitrateBps { get; }

    /// <summary>Coarse network quality behind the recommendation, or <see langword="null"/> when inactive.</summary>
    public NetworkQuality? NetworkQuality { get; }

    /// <summary>Creates the event payload.</summary>
    public VideoBitrateRecommendationEventArgs(long? recommendedBitrateBps, NetworkQuality? networkQuality)
    {
        RecommendedBitrateBps = recommendedBitrateBps;
        NetworkQuality = networkQuality;
    }
}
