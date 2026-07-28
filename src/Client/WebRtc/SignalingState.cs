namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The RFC 8829 §4.1.3 signalling state of a WebRTC <see cref="IPeerConnection"/> — the offer/answer
/// half of the peer's lifecycle, distinct from the ICE/DTLS transport lifecycle exposed by
/// <see cref="PeerConnectionState"/>. It mirrors the W3C <c>RTCSignalingState</c>, tracking where the
/// peer sits in the SDP negotiation: whether an offer is pending locally, pending from the remote, or
/// the exchange has settled.
/// <para>
/// This SDK negotiates a single offer/answer exchange per peer (re-offer / renegotiation is a later
/// package), so the state moves <c>Stable → HaveLocalOffer → Stable</c> (offerer) or
/// <c>Stable → HaveRemoteOffer → Stable</c> (answerer) and then to <see cref="Closed"/>. The
/// provisional-answer state (<c>have-local-pranswer</c> / <c>have-remote-pranswer</c>) is not used by
/// this SDK — no <c>a=pranswer</c> path exists — and is therefore not modelled.
/// </para>
/// </summary>
public enum SignalingState
{
    /// <summary>
    /// No offer/answer exchange is in progress: either none has started, or the last one completed. This is
    /// the initial state and the resting state between negotiations (RFC 8829 §4.1.3 <c>stable</c>).
    /// </summary>
    Stable = 0,

    /// <summary>
    /// A local offer has been produced (<see cref="IPeerConnection.CreateOffer"/>) and applied, and the peer
    /// is waiting for the remote answer (RFC 8829 §4.1.3 <c>have-local-offer</c> — the offerer role).
    /// </summary>
    HaveLocalOffer,

    /// <summary>
    /// A remote offer has been applied and the peer is producing the local answer (RFC 8829 §4.1.3
    /// <c>have-remote-offer</c> — the answerer role). Transient: the answerer moves through this state and
    /// back to <see cref="Stable"/> within a single <see cref="IPeerConnection.SetRemoteDescriptionAsync"/>.
    /// </summary>
    HaveRemoteOffer,

    /// <summary>
    /// The peer has been closed; no further negotiation is possible (RFC 8829 §4.1.3 <c>closed</c>).
    /// </summary>
    Closed,
}
