namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The WebRTC peer facade — the happy-path entry point for building signalling-neutral peer
/// connections. Mirrors the SIP <c>IVoipClient</c>; the advanced peer-registry manager tier is added in a
/// later slice (ADR-012).
/// </summary>
public interface IWebRtcClient
{
    /// <summary>
    /// Creates a new peer connection with the client's configured codecs, DTLS identity and local
    /// endpoint. The caller drives signalling and disposes the returned peer.
    /// </summary>
    IPeerConnection CreatePeer();

    /// <summary>
    /// The registry of optional facade extensions contributed by separate packages (L3 plugin seam);
    /// register modules programmatically via <see cref="IWebRtcModuleRegistry.Register"/>.
    /// </summary>
    IWebRtcModuleRegistry Modules { get; }
}
