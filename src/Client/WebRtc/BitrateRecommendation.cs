using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A ready-to-use send-bitrate recommendation for a peer connection, derived by the SDK from transport-wide
/// congestion control (transport-cc / RFC 8888). The SDK does the estimation and hands back a finished
/// recommendation — the app sets its encoder (or, for an SFU, selects the simulcast layer to forward to this
/// receiver) to this value; it never has to interpret raw delay/loss metrics. Carried by
/// <see cref="IPeerConnection.RecommendedBitrateChanged"/> and mirrored point-in-time by
/// <see cref="IPeerConnection.RecommendedOutgoingBitrateBps"/>.
/// </summary>
public readonly struct BitrateRecommendation
{
    /// <summary>
    /// Creates a recommendation carrying the recommended send bitrate and the coarse network quality it was
    /// derived under.
    /// </summary>
    /// <param name="bitrateBps">The recommended outbound bitrate in bits per second.</param>
    /// <param name="quality">The coarse network quality the recommendation was derived under.</param>
    public BitrateRecommendation(long bitrateBps, NetworkQuality quality)
    {
        BitrateBps = bitrateBps;
        Quality = quality;
    }

    /// <summary>The recommended outbound bitrate in bits per second — set the encoder / forwarded layer to this.</summary>
    public long BitrateBps { get; }

    /// <summary>The coarse network quality (good / fair / poor) the recommendation was derived under.</summary>
    public NetworkQuality Quality { get; }
}
