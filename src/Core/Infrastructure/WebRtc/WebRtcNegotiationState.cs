using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// The offer/answer half of a WebRTC peer's lifecycle (RFC 8829 §4.1.3): the signalling state and the two
/// descriptions that move with it. Each method below is one named transition of that state machine, carrying the
/// guard that says which states it is legal from — so the rules live in one place instead of being restated in
/// each negotiation path.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="WebRtcPeerConnection"/> to keep that file under the size limit. It shares the
/// owning peer's gate rather than taking its own, because the signalling state is written together with the peer's
/// session and remote inventory and must stay one atomic step — C# monitors are reentrant, so the peer takes
/// <c>_sync</c> around a group of writes and calls in here for its own part.
/// </para>
/// <para>
/// It deliberately does <b>not</b> raise the change event. The peer fires it at points that are not always
/// immediately after the lock — settling to Stable fires it <em>after</em> the session is wired and the connection
/// state has moved to Connecting — and that observable ordering is part of the contract, so the raise stays at the
/// call sites where it can be placed exactly.
/// </para>
/// </remarks>
internal sealed class WebRtcNegotiationState
{
    private readonly object _gate;
    private WebRtcSignalingState _state = WebRtcSignalingState.Stable;
    private string? _localDescription;
    private string? _remoteDescription;
    private SdpSessionDescription? _localOfferModel;

    /// <param name="gate">The owning peer's lock, shared so the state keeps its original serialisation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> is <see langword="null"/>.</exception>
    public WebRtcNegotiationState(object gate) => _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>The current RFC 8829 §4.1.3 signalling state.</summary>
    public WebRtcSignalingState Current
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>The local description in force (the offer or the answer), or null before the first one.</summary>
    public string? LocalDescription
    {
        get { lock (_gate) { return _localDescription; } }
    }

    /// <summary>The applied remote description, or null before one is applied.</summary>
    public string? RemoteDescription
    {
        get { lock (_gate) { return _remoteDescription; } }
    }

    /// <summary>
    /// createOffer + setLocalDescription (RFC 8829 §4.1.3). Valid from Stable (the first offer) and idempotent
    /// from HaveLocalOffer (a re-offer before any answer replaces the pending one, state unchanged). Any other
    /// state — a remote offer pending, or the peer closed — is an invalid transition and fails loudly rather than
    /// silently overwriting negotiation state.
    /// </summary>
    /// <param name="model">The offer model, kept as the offerer/answerer discriminator for the next cycle.</param>
    /// <param name="sdp">The serialised offer, which becomes the local description.</param>
    /// <returns><see langword="true"/> when this crossed the Stable → HaveLocalOffer edge, so the caller raises
    /// the change event; a re-offer within HaveLocalOffer is no transition and fires nothing.</returns>
    /// <exception cref="InvalidOperationException">An offer is not valid from the current state.</exception>
    public bool EnterHaveLocalOffer(SdpSessionDescription model, string sdp)
    {
        lock (_gate)
        {
            if (_state is not (WebRtcSignalingState.Stable or WebRtcSignalingState.HaveLocalOffer))
                throw new InvalidOperationException(
                    $"Cannot create an offer in signalling state '{_state}': an offer is valid only " +
                    "from Stable or HaveLocalOffer (RFC 8829 §4.1.3).");

            _localOfferModel = model;
            _localDescription = sdp;
            var entered = _state == WebRtcSignalingState.Stable;
            _state = WebRtcSignalingState.HaveLocalOffer;
            return entered;
        }
    }

    /// <summary>
    /// setRemoteDescription on the first cycle (RFC 8829 §4.1.3): valid from HaveLocalOffer (the offerer applying
    /// the answer) or Stable (the answerer applying the offer). Returns the offerer snapshot taken under the gate
    /// — a non-null pending offer means this peer is the offerer — and moves an answerer to HaveRemoteOffer so the
    /// two-transition answerer path is observable.
    /// </summary>
    /// <returns>The pending local offer and its serialised form; both null for an answerer.</returns>
    /// <exception cref="InvalidOperationException">A remote description is not valid from the current state.</exception>
    public (SdpSessionDescription? PendingOffer, string? PendingLocalDescription) BeginApplyRemote()
    {
        lock (_gate)
        {
            RequireRemoteApplicable();

            // One snapshot under the gate (HARD-C6): the local offer model is the offerer/answerer discriminator
            // the session build uses, and it must stay consistent with the description the caller returns.
            var pendingOffer = _localOfferModel;
            var pendingLocal = _localDescription;
            if (pendingOffer is null)
                _state = WebRtcSignalingState.HaveRemoteOffer;
            return (pendingOffer, pendingLocal);
        }
    }

    /// <summary>
    /// setRemoteDescription on a running session — a renegotiation cycle. Same legal states as the first cycle,
    /// but the offerer/answerer discriminator is the signalling state rather than "was an offer ever created"
    /// (that stays set after cycle 1): HaveLocalOffer means a fresh re-offer was created here and this remote is
    /// its answer; Stable means this remote is a new offer to answer. An answerer moves to HaveRemoteOffer.
    /// </summary>
    /// <returns>Whether this peer answers, plus the local description in force (the re-offer, for the offerer).</returns>
    /// <exception cref="InvalidOperationException">A remote description is not valid from the current state.</exception>
    public (bool IsAnswerer, SdpSessionDescription LocalModel, string LocalSdp) BeginRenegotiate()
    {
        lock (_gate)
        {
            RequireRemoteApplicable();

            var isAnswerer = _state == WebRtcSignalingState.Stable;
            if (isAnswerer)
                _state = WebRtcSignalingState.HaveRemoteOffer;
            return (isAnswerer, _localOfferModel!, _localDescription!);
        }
    }

    /// <summary>
    /// Settles to Stable with the exchange complete (RFC 8829 §4.1.3) — the offerer from HaveLocalOffer (answer
    /// applied), the answerer from HaveRemoteOffer (answer produced). The caller raises the change event itself,
    /// at the point in its sequence where the event belongs.
    /// </summary>
    /// <param name="remoteSdp">The applied remote description.</param>
    /// <param name="localSdp">This peer's description for the completed exchange.</param>
    public void SettleStable(string remoteSdp, string localSdp)
    {
        lock (_gate)
        {
            _remoteDescription = remoteSdp;
            _localDescription = localSdp;
            _state = WebRtcSignalingState.Stable;
        }
    }

    /// <summary>
    /// Rolls a failed answer back to Stable. Without it the peer would be stranded in HaveRemoteOffer, which both
    /// the entry guard and <see cref="EnterHaveLocalOffer"/> reject — so a later attempt would be impossible even
    /// though the running session is intact.
    /// </summary>
    public void RollBackToStable()
    {
        lock (_gate) { _state = WebRtcSignalingState.Stable; }
    }

    /// <summary>
    /// Terminates signalling at Closed (RFC 8829 §4.1.3). Idempotent across a double dispose.
    /// </summary>
    /// <returns><see langword="true"/> when this call closed it, so the caller raises the change event once.</returns>
    public bool Close()
    {
        lock (_gate)
        {
            var closed = _state != WebRtcSignalingState.Closed;
            _state = WebRtcSignalingState.Closed;
            return closed;
        }
    }

    // The shared legality rule for applying a remote description: HaveRemoteOffer (one is already pending) and
    // Closed are invalid transitions. The caller must hold the gate.
    private void RequireRemoteApplicable()
    {
        if (_state is not (WebRtcSignalingState.Stable or WebRtcSignalingState.HaveLocalOffer))
            throw new InvalidOperationException(
                $"Cannot apply a remote description in signalling state '{_state}' (RFC 8829 §4.1.3).");
    }
}
