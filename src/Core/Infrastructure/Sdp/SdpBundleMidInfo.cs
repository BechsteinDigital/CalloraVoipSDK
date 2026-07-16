namespace CalloraVoipSdk.Core.Infrastructure.Sdp;

/// <summary>
/// The negotiated BUNDLE MID facts recovered from an SDP (RFC 8843 §9 / RFC 9143): the shared
/// <c>sdes:mid</c> header-extension id and each m-line's <c>a=mid</c> token. The id is one value for the
/// whole group — it is the same on every bundled m-line (RFC 8843 §9). A per-m-line MID is null when
/// that m-line is absent. These are the BUNDLE-specific facts the media parameters do not carry today,
/// so the layer that builds a <c>BundledMediaSession</c> reads them here from the negotiated SDP.
/// </summary>
internal sealed record SdpBundleMidInfo(byte MidExtensionId, string? AudioMid, string? VideoMid);
