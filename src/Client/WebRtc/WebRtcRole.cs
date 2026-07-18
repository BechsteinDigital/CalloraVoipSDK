namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The negotiation role a peer takes in the SDK-driven handshake
/// (<see cref="WebRtcPeerConnectionExtensions.ConnectAsync"/>).
/// </summary>
public enum WebRtcRole
{
    /// <summary>The caller: creates the offer, sends it, then applies the remote answer (RFC 8829).</summary>
    Offerer,

    /// <summary>The callee: receives the remote offer, produces the answer, then sends it back.</summary>
    Answerer,
}
