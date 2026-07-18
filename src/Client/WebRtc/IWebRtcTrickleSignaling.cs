namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Extends <see cref="IWebRtcSignaling"/> with out-of-band ICE candidate trickle (RFC 8838): alongside the
/// offer/answer, the SDK sends each locally gathered candidate as it is discovered and applies remote
/// candidates as they arrive, rather than relying only on the candidates the SDP carried. When an app
/// supplies this on <see cref="WebRtcPeerConnectionExtensions.ConnectAsync"/>, the SDK gathers
/// server-reflexive candidates and runs the trickle exchange; a plain <see cref="IWebRtcSignaling"/> stays
/// SDP-only. Candidate strings are RFC 8829 <c>candidate:…</c> lines — the wire form browsers exchange
/// verbatim, so no conversion is needed at the app boundary.
/// </summary>
public interface IWebRtcTrickleSignaling : IWebRtcSignaling
{
    /// <summary>
    /// Sends one locally gathered ICE candidate (an RFC 8829 <c>candidate:</c> line) to the remote peer over
    /// the app's channel.
    /// </summary>
    Task SendCandidateAsync(string candidate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Awaits the next remote ICE candidate line, or <see langword="null"/> when the remote has signalled
    /// end-of-candidates (RFC 8840) — after which the SDK stops polling for more.
    /// </summary>
    Task<string?> ReceiveCandidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals that local gathering is complete and no further local candidates will be sent (RFC 8840
    /// <c>a=end-of-candidates</c> semantics, carried out-of-band).
    /// </summary>
    Task SendEndOfCandidatesAsync(CancellationToken cancellationToken = default);
}
