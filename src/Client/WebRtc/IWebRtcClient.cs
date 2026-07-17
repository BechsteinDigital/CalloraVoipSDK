namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The WebRTC peer facade — the happy-path entry point for building signalling-neutral peer
/// connections. Mirrors the SIP <c>IVoipClient</c>; the advanced manager tier (peer registry, media,
/// quality) and the signalling-adapter convenience are added in later slices (ADR-012).
/// </summary>
public interface IWebRtcClient
{
    /// <summary>
    /// Creates a new peer connection with the client's configured codecs, DTLS identity and local
    /// endpoint. The caller drives signalling and disposes the returned peer.
    /// </summary>
    IPeerConnection CreatePeer();
}
