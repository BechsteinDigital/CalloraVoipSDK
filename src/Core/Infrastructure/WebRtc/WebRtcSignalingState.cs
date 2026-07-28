namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// The RFC 8829 §4.1.3 signalling state of a <see cref="WebRtcPeerConnection"/> — the offer/answer half of
/// the peer's lifecycle, distinct from the ICE/DTLS transport lifecycle in <see cref="WebRtcConnectionState"/>.
/// It mirrors the W3C <c>RTCSignalingState</c>. This peer negotiates a single offer/answer exchange
/// (re-offer / renegotiation is a later package), so the state moves <c>Stable → HaveLocalOffer → Stable</c>
/// (offerer) or <c>Stable → HaveRemoteOffer → Stable</c> (answerer), then to <see cref="Closed"/>. The
/// provisional-answer states (<c>have-*-pranswer</c>) are not used — no <c>a=pranswer</c> path exists — so
/// they are not modelled. The public projection is <c>CalloraVoipSdk.WebRtc.SignalingState</c>.
/// </summary>
internal enum WebRtcSignalingState
{
    /// <summary>No offer/answer exchange in progress (initial and resting state); RFC 8829 <c>stable</c>.</summary>
    Stable = 0,

    /// <summary>A local offer was produced and applied; awaiting the remote answer; RFC 8829 <c>have-local-offer</c>.</summary>
    HaveLocalOffer,

    /// <summary>A remote offer was applied; producing the local answer; RFC 8829 <c>have-remote-offer</c>.</summary>
    HaveRemoteOffer,

    /// <summary>The peer was closed; no further negotiation is possible; RFC 8829 <c>closed</c>.</summary>
    Closed,
}
