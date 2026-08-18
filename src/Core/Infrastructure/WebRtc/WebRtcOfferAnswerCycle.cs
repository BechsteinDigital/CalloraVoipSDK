using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// The peer state an offer/answer cycle drives, and the peer behaviour it triggers. Passed as one record so the
/// cycle can own the negotiation choreography without owning the peer's fields — everything guarded stays behind
/// these delegates, which take the peer's gate themselves.
/// </summary>
/// <param name="SnapshotSession">The live session and whether the peer was started, read as one atomic snapshot.</param>
/// <param name="EnsureLocalEndPoint">Binds the shared media socket if needed and returns its endpoint.</param>
/// <param name="MediaOptions">Builds the SDP media options for a description at that endpoint.</param>
/// <param name="BuildSession">Builds the BUNDLE media session from both descriptions; null for a non-bundle exchange.</param>
/// <param name="CommitSession">
/// Commits a completed exchange in one atomic step: the session, the socket hand-over, the remote inventory and
/// the settled signalling state. Called with the same session on a renegotiation, where only the latter two move.
/// </param>
/// <param name="OnSessionBuilt">Wires a newly built session's events and hands it the buffered trickle candidates.</param>
/// <param name="TransitionTo">Moves the transport-side connection state.</param>
/// <param name="RaiseSignalingState">Fires the signalling-state change event for a transition already committed.</param>
/// <param name="EmitLocalHosts">Emits the local host candidates for the bound endpoint (RFC 8838 trickle).</param>
internal sealed record WebRtcOfferAnswerHost(
    Func<(BundledMediaSession? Session, bool Started)> SnapshotSession,
    Func<IPEndPoint> EnsureLocalEndPoint,
    Func<IPEndPoint, SdpMediaOptions> MediaOptions,
    Func<SdpSessionDescription, SdpSessionDescription, bool, BundledMediaSession?> BuildSession,
    Action<BundledMediaSession?, SdpSessionDescription, string, string> CommitSession,
    Action<BundledMediaSession> OnSessionBuilt,
    Action<WebRtcConnectionState> TransitionTo,
    Action<WebRtcSignalingState> RaiseSignalingState,
    Action<IPEndPoint> EmitLocalHosts);

/// <summary>
/// The RFC 8829 offer/answer choreography of a WebRTC peer: applying a remote description, deciding whether it
/// opens the first exchange or renegotiates a running session, producing the local description, and moving the
/// signalling and connection states through the sequence in the order an application observes them.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="WebRtcPeerConnection"/>, which had grown to the size limit with this as its largest
/// part. The split is along a real seam: the peer owns identity, media and lifetime; this owns the negotiation
/// that moves between them. The legality rules of the state machine itself live one level further down, in
/// <see cref="WebRtcNegotiationState"/>.
/// </para>
/// <para>
/// Ordering is the contract here, not an implementation detail. An answerer fires HaveRemoteOffer before it
/// negotiates and Stable after; the session is published and wired before the connection state moves to
/// Connecting; and a failed answer rolls signalling back to Stable so a later attempt is possible. Those
/// sequences are observable through the peer's events, so they are preserved exactly as they were inline.
/// </para>
/// </remarks>
internal sealed class WebRtcOfferAnswerCycle
{
    private readonly WebRtcNegotiationState _negotiation;
    private readonly ISdpOfferAnswerNegotiator _negotiator;
    private readonly ISdpSessionParser _parser;
    private readonly ISdpSessionSerializer _serializer;
    private readonly IReadOnlyList<SdpCodecDefinition> _audioCodecs;
    private readonly WebRtcRenegotiator _renegotiator;
    private readonly ILogger _logger;
    private readonly WebRtcOfferAnswerHost _host;

    /// <param name="negotiation">The signalling state and descriptions this cycle transitions.</param>
    /// <param name="negotiator">Produces offers and answers from the SDP model.</param>
    /// <param name="parser">Parses the untrusted remote SDP (capped, non-throwing).</param>
    /// <param name="serializer">Serialises a produced description.</param>
    /// <param name="audioCodecs">The peer's configured audio codecs.</param>
    /// <param name="renegotiator">Applies the track-set diff and any ICE restart on a running session.</param>
    /// <param name="logger">The owning peer's logger.</param>
    /// <param name="host">The peer state and behaviour this cycle drives.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public WebRtcOfferAnswerCycle(
        WebRtcNegotiationState negotiation,
        ISdpOfferAnswerNegotiator negotiator,
        ISdpSessionParser parser,
        ISdpSessionSerializer serializer,
        IReadOnlyList<SdpCodecDefinition> audioCodecs,
        WebRtcRenegotiator renegotiator,
        ILogger logger,
        WebRtcOfferAnswerHost host)
    {
        _negotiation = negotiation ?? throw new ArgumentNullException(nameof(negotiation));
        _negotiator = negotiator ?? throw new ArgumentNullException(nameof(negotiator));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _audioCodecs = audioCodecs ?? throw new ArgumentNullException(nameof(audioCodecs));
        _renegotiator = renegotiator ?? throw new ArgumentNullException(nameof(renegotiator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Applies a remote description and returns this peer's local one — the whole RFC 8829 offer/answer cycle,
    /// first exchange and renegotiation alike. See <see cref="WebRtcPeerConnection.SetRemoteDescriptionAsync"/>
    /// for the caller-facing contract.
    /// </summary>
    /// <param name="remoteSdp">The peer's SDP.</param>
    /// <param name="cancellationToken">Cancels before any state is touched.</param>
    /// <exception cref="ArgumentException">The remote description is missing or not valid SDP.</exception>
    /// <exception cref="InvalidOperationException">The state forbids it, or no answer could be negotiated.</exception>
    public Task<string> ApplyRemoteAsync(string remoteSdp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteSdp);
        cancellationToken.ThrowIfCancellationRequested();

        // Untrusted remote SDP over the public SetRemoteDescription path (no SIP transport body cap):
        // the capped, non-throwing parser rejects an over-limit or malformed body as a controlled failure.
        if (!_parser.TryParse(remoteSdp, out var remote))
            throw new ArgumentException("The remote description is not valid SDP.", nameof(remoteSdp));

        SdpSessionDescription? pendingOffer;
        string? pendingLocalDescription;

        // One snapshot: a second offer/answer cycle on a running session is renegotiation (RFC 8829 P3b-3),
        // which keeps the shared transport/DTLS/ICE/SRTP and applies only the track-set diff.
        var (liveSession, started) = _host.SnapshotSession();

        // A session that was already started but never built (StartAsync on a non-bundle exchange) has no
        // renegotiation path — the diff needs a live session. Fail loudly rather than rebuild mid-flight.
        if (liveSession is null && started)
            throw new InvalidOperationException(
                "Cannot apply a remote description after StartAsync without a media session; " +
                "dispose this peer and create a new one.");

        if (liveSession is not null)
            return RenegotiateAsync(liveSession, remoteSdp, remote);

        // An answerer enters HaveRemoteOffer here so the two-transition path is observable (the event fires
        // below, outside the lock); the offerer stays in HaveLocalOffer until the answer is applied.
        (pendingOffer, pendingLocalDescription) = _negotiation.BeginApplyRemote();

        // Answerer's first transition: fire outside the lock (K3). A negotiation failure below still leaves the
        // peer in HaveRemoteOffer, mirroring W3C where a failed createAnswer does not roll signalling back.
        if (pendingOffer is null)
            _host.RaiseSignalingState(WebRtcSignalingState.HaveRemoteOffer);

        SdpSessionDescription localModel;
        string localSdp;
        IPEndPoint? answererLocal = null;
        if (pendingOffer is not null)
        {
            // Offerer: the remote description is the answer. Fail closed unless it is a valid RFC 3264 §6
            // response to our offer, before building any transport or track (P1-b).
            if (SdpAnswerValidator.Validate(pendingOffer, remote) is { } answerViolation)
            {
                _host.TransitionTo(WebRtcConnectionState.Failed);
                throw new InvalidOperationException($"Remote answer is not a valid response to the local offer: {answerViolation}");
            }

            localModel = pendingOffer;
            localSdp = pendingLocalDescription!;
        }
        else
        {
            // Answerer: the remote description is the offer; negotiate our answer.
            var local = _host.EnsureLocalEndPoint();
            answererLocal = local;
            var result = _negotiator.NegotiateAnswer(
                remote, local, _audioCodecs, SdpMediaDirection.SendRecv, _host.MediaOptions(local));
            if (!result.Success || result.Answer is null)
            {
                _host.TransitionTo(WebRtcConnectionState.Failed);
                throw new InvalidOperationException("Could not negotiate an answer for the remote description.");
            }

            localModel = result.Answer;
            localSdp = _serializer.Serialize(result.Answer);
        }

        // Build the shared DTLS-SRTP/BUNDLE media transport from both descriptions; a non-bundle exchange
        // yields no session (logged; StartAsync surfaces it). The offerer holds the ICE controlling role
        // (RFC 8445 §6.1.1); a relay ICE local candidate rides the socket only when a TURN allocation was
        // already gathered on it (the answerer adopts its allocation later — a follow-up).
        var session = _host.BuildSession(remote, localModel, /* iceControlling: the offerer holds the role */ pendingOffer is not null);
        if (session is null)
            _logger.LogWarning("The remote description did not negotiate a BUNDLE media session; no transport was built.");

        // One atomic step on the peer: the session, its socket hand-over, the remote inventory and the settled
        // signalling state commit together, so nothing observes a peer that is Stable but has no session.
        _host.CommitSession(session, remote, remoteSdp, localSdp);

        // Publish _session before wiring its event handlers, so a state-transition callback can never
        // fire against a peer that has not yet recorded the session it belongs to (HARD-C6).
        if (session is not null)
            _host.OnSessionBuilt(session);

        _host.TransitionTo(WebRtcConnectionState.Connecting);
        _host.RaiseSignalingState(WebRtcSignalingState.Stable);
        if (answererLocal is not null)
            _host.EmitLocalHosts(answererLocal);
        return Task.FromResult(localSdp);
    }

    // Applies a second offer/answer cycle to the running session as a track-set diff (RFC 8829 renegotiation,
    // P3b-3): no transport/DTLS/ICE/SRTP rebuild — only the live add/deactivate on the existing session. The
    // signalling state runs the same RFC 8829 §4.1.3 transitions as the first cycle.
    private async Task<string> RenegotiateAsync(
        BundledMediaSession session, string remoteSdp, SdpSessionDescription remote)
    {
        // Offerer: our re-offer (already produced by CreateOffer) is the local description and this remote is its
        // answer. Answerer: negotiate below; it has entered HaveRemoteOffer, whose event fires outside the lock.
        var (isAnswerer, newLocalModel, newLocalSdp) = _negotiation.BeginRenegotiate();

        // Compute + apply the video-track diff on the live session — outside _sync, since AddVideoTrack /
        // SetVideoTrackInactive take the session's own track-mutation gate (K3). The renegotiator rejects an ICE
        // restart (a rotated remote ICE ufrag). A failure leaves the running tracks untouched and the caller sees it.
        IPEndPoint? answererLocal = null;
        if (isAnswerer)
        {
            _host.RaiseSignalingState(WebRtcSignalingState.HaveRemoteOffer);
            answererLocal = _host.EnsureLocalEndPoint();
            try
            {
                newLocalModel = await _renegotiator.NegotiateAnswerAndApplyAsync(
                    session, remote,
                    new WebRtcRenegotiationAnswerContext(_negotiator, answererLocal, _audioCodecs, () => _host.MediaOptions(answererLocal)));
            }
            catch
            {
                // A failed re-answer throws before any track mutation (running session intact), but would strand
                // the peer in HaveRemoteOffer (both the entry guard and CreateOffer reject that). Roll signalling
                // back to Stable so a later attempt is possible, then re-throw (not swallowed).
                _negotiation.RollBackToStable();
                _host.RaiseSignalingState(WebRtcSignalingState.Stable);
                throw;
            }
            newLocalSdp = _serializer.Serialize(newLocalModel);
        }
        else
        {
            // P1-b: a re-answer gets the same RFC 3264 §6 validation; a bad one rolls signalling back to
            // Stable (the live session stays intact) and throws, mirroring the answerer failure path.
            if (SdpAnswerValidator.Validate(newLocalModel, remote) is { } reViolation)
            {
                _negotiation.RollBackToStable();
                _host.RaiseSignalingState(WebRtcSignalingState.Stable);
                throw new InvalidOperationException($"Remote re-answer is not a valid response to the local re-offer: {reViolation}");
            }

            await _renegotiator.ApplyReAnswerAsync(session, newLocalModel, remote);
        }

        // Refresh the remote track identity/inventory from the new description (P2c: the receiver re-materialises
        // its remote tracks from this). The transport is unchanged; only the advertised track set moved.
        _host.CommitSession(session, remote, remoteSdp, newLocalSdp);

        _host.RaiseSignalingState(WebRtcSignalingState.Stable);
        if (answererLocal is not null)
            _host.EmitLocalHosts(answererLocal);
        return newLocalSdp;
    }
}
