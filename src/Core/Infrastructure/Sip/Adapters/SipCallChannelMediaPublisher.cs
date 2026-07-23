using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>Outcome of a media-parameter publication attempt.</summary>
internal enum MediaPublicationResult
{
    Published,
    Skipped,
    PolicyViolation
}

/// <summary>
/// Snapshot of the channel's live negotiated context needed to publish media parameters.
/// Built lazily by the channel only when a publication actually reaches the parse step, so the
/// route-address probe and SDP-option build stay exactly as lazy as the inline code they replaced.
/// </summary>
internal readonly record struct MediaPublishContext(
    IPEndPoint LocalEndPoint,
    SdpMediaNegotiationOptions? SdpOptions,
    CallIceLocalDescription? LocalIceDescription,
    bool IceControlling,
    string? LocalAnswerOrOfferSdp);

/// <summary>
/// Owns the per-call media-parameter publication: parses the negotiated SDP, enriches it with
/// ICE/SRTP/DTLS metadata, applies the SRTP policy, and fires the channel's
/// <c>MediaParametersNegotiated</c> event at most once per session — with an additive re-publish on a
/// re-INVITE rekey. Extracted from <see cref="SipCoreCallChannel"/> so the channel adapter no longer
/// carries the publication guard, the rekey signature, and the live-keying state inline.
/// </summary>
internal sealed class SipCallChannelMediaPublisher
{
    private readonly ISdpNegotiator _sdpNegotiator;
    private readonly SipCallChannelSrtpTelemetry _srtpTelemetry;
    private readonly SrtpPolicy _appliedSrtpPolicy;
    private readonly ILogger _logger;
    private readonly Func<ISipCallSession, MediaPublishContext> _contextFactory;
    private readonly Action _releasePortReservationSockets;
    private readonly Action<CallMediaParameters> _fireNegotiated;
    private readonly Func<ISipCallSession, string, Task> _terminateForPolicyViolation;

    private int _mediaParametersFired;

    // Signature of the last published media parameters (SRTP keys + remote endpoint + codec).
    // A re-INVITE whose negotiated media differs re-publishes (rekey); an identical one (a
    // retransmission) does not, so media never churns without an actual change.
    private volatile string? _lastPublishedSignature;

    // The outbound encrypt keys of the running SRTP media context (null when the call is plain RTP).
    // A hold/unhold re-offer reuses them so the offered a=crypto stays identical to the live context.
    // A DTLS-keyed leg latches instead so re-offers keep signaling DTLS. Read on the signaling thread
    // that issues hold/unhold, so kept volatile.
    private volatile string? _activeLocalSrtpKeyParams;
    private volatile string? _activeLocalVideoSrtpKeyParams;
    private volatile bool _dtlsActiveOnCall;

    public SipCallChannelMediaPublisher(
        ISdpNegotiator sdpNegotiator,
        SipCallChannelSrtpTelemetry srtpTelemetry,
        SrtpPolicy appliedSrtpPolicy,
        ILogger logger,
        Func<ISipCallSession, MediaPublishContext> contextFactory,
        Action releasePortReservationSockets,
        Action<CallMediaParameters> fireNegotiated,
        Func<ISipCallSession, string, Task> terminateForPolicyViolation)
    {
        _sdpNegotiator = sdpNegotiator;
        _srtpTelemetry = srtpTelemetry;
        _appliedSrtpPolicy = appliedSrtpPolicy;
        _logger = logger;
        _contextFactory = contextFactory;
        _releasePortReservationSockets = releasePortReservationSockets;
        _fireNegotiated = fireNegotiated;
        _terminateForPolicyViolation = terminateForPolicyViolation;
    }

    /// <summary>True once media parameters have been published for the current session.</summary>
    public bool HasFired => Volatile.Read(ref _mediaParametersFired) != 0;

    /// <summary>Live outbound SDES key for the audio m-line; null on a plain-RTP call.</summary>
    public string? ActiveLocalSrtpKeyParams => _activeLocalSrtpKeyParams;

    /// <summary>Live outbound SDES key for the video m-line; null when there is no SDES-keyed video.</summary>
    public string? ActiveLocalVideoSrtpKeyParams => _activeLocalVideoSrtpKeyParams;

    /// <summary>True once a leg negotiated DTLS, so re-offers keep signaling it.</summary>
    public bool DtlsActiveOnCall => _dtlsActiveOnCall;

    /// <summary>
    /// Resets the once-per-session publication guard and rekey signature when the channel rebinds to
    /// a fresh session (a 3xx redirect creates a new dialog for a new target).
    /// </summary>
    public void ResetForRebind()
    {
        Interlocked.Exchange(ref _mediaParametersFired, 0);
        _lastPublishedSignature = null;
    }

    /// <summary>
    /// Parses and publishes negotiated media parameters.
    /// Guarded so it fires at most once per session (re-INVITE will fire again).
    /// </summary>
    public MediaPublicationResult TryPublish(ISipCallSession session, out string reasonCode)
    {
        reasonCode = SrtpDecisionReasonCodes.NotEvaluated;

        // Allow re-fire on re-INVITE (reset guard on new session).
        if (Interlocked.Exchange(ref _mediaParametersFired, 1) != 0)
            return MediaPublicationResult.Published;

        return PublishFrom(session, session.RemoteSdp, out reasonCode);
    }

    // Publishes media parameters from an early-media (180/183) SDP so a receive-only session can start
    // before the 200 OK (F011 slice 3b). SDES works as offerer (our key in the retained offer, the peer
    // key in the 183). A parse/policy failure releases the guard so the 200-OK publish runs normally. If
    // the early SDP equals the later answer (Asterisk Progress->Answer), the Established transition sees
    // an unchanged signature and the session runs through without a rebuild.
    public void TryPublishEarly(ISipCallSession session)
    {
        var earlySdp = session.EarlyMediaSdp;
        if (string.IsNullOrWhiteSpace(earlySdp))
            return;
        if (Interlocked.Exchange(ref _mediaParametersFired, 1) != 0)
            return;
        if (PublishFrom(session, earlySdp, out _) != MediaPublicationResult.Published)
            Interlocked.Exchange(ref _mediaParametersFired, 0); // release so the 200-OK path publishes
    }

    // Shared core: parses `remoteSdp`, enriches/validates, releases the port-reservation sockets and
    // fires MediaParametersNegotiated. Used by the final 200-OK publish and the early-media publish
    // (F011). The caller owns the once-per-session `_mediaParametersFired` guard.
    private MediaPublicationResult PublishFrom(
        ISipCallSession session,
        string? remoteSdp,
        out string reasonCode)
    {
        reasonCode = SrtpDecisionReasonCodes.NotEvaluated;

        if (string.IsNullOrWhiteSpace(remoteSdp))
        {
            _logger.LogWarning("No remote SDP available for call {CallId}; RTP will not start.", session.CallId);
            reasonCode = SrtpDecisionReasonCodes.MediaParametersUnavailable;
            _srtpTelemetry.PublishDecision(session, isSrtpNegotiated: false, profile: string.Empty, reasonCode, violatesPolicy: _appliedSrtpPolicy == SrtpPolicy.Required);
            return _appliedSrtpPolicy == SrtpPolicy.Required
                ? MediaPublicationResult.PolicyViolation
                : MediaPublicationResult.Skipped;
        }

        // Same address the SDP advertises: RTP/RTCP must bind where the peer sends to,
        // and a loopback/wildcard signaling bind is not routable for a LAN peer.
        var ctx = _contextFactory(session);

        var parameters = _sdpNegotiator.TryParseMediaParameters(remoteSdp, ctx.LocalEndPoint, ctx.SdpOptions);
        if (parameters is null)
        {
            _logger.LogWarning("Failed to parse remote SDP for call {CallId}; RTP will not start.", session.CallId);
            reasonCode = SrtpDecisionReasonCodes.MediaParametersUnavailable;
            _srtpTelemetry.PublishDecision(session, isSrtpNegotiated: false, profile: string.Empty, reasonCode, violatesPolicy: _appliedSrtpPolicy == SrtpPolicy.Required);
            return _appliedSrtpPolicy == SrtpPolicy.Required
                ? MediaPublicationResult.PolicyViolation
                : MediaPublicationResult.Skipped;
        }

        var withIceMetadata = CallMediaParametersIceEnricher.Enrich(parameters, ctx.LocalIceDescription, ctx.IceControlling);
        reasonCode = SrtpPolicyEvaluator.ResolveReasonCode(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        var violatesPolicy = SrtpPolicyEvaluator.IsPolicyViolation(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        var enrichedParameters = CallMediaParametersDtlsEnricher.Enrich(
            CallMediaParametersSrtpEnricher.Enrich(
                withIceMetadata, reasonCode, remoteSdp, ctx.LocalAnswerOrOfferSdp, _appliedSrtpPolicy),
            remoteSdp, ctx.LocalAnswerOrOfferSdp);

        // Fail closed on a keyless secure negotiation: the exchange signals SRTP (secure
        // profile / fingerprint) but produced neither SDES keys nor a DTLS association —
        // e.g. a UDP/TLS answer without a fingerprint. Under Required this is a policy
        // violation; the media layer additionally stays fail-closed (RequireEncryptedMedia).
        if (IsKeylessSecureNegotiation(enrichedParameters))
        {
            reasonCode = SrtpDecisionReasonCodes.RequiredNegotiationFailed;
            violatesPolicy = _appliedSrtpPolicy == SrtpPolicy.Required;
        }

        // Remember the live outbound encrypt key so a later hold/unhold re-offers the same
        // key (keeps SRTP without rekeying); null when the call resolved to plain RTP.
        // A DTLS-keyed leg latches instead so re-offers keep signaling DTLS.
        _activeLocalSrtpKeyParams = enrichedParameters.SrtpLocalKeyParams;
        _activeLocalVideoSrtpKeyParams = enrichedParameters.Video?.SrtpLocalKeyParams;
        _dtlsActiveOnCall = enrichedParameters.IsDtlsNegotiated;
        _srtpTelemetry.PublishDecision(
            session,
            enrichedParameters.IsSrtpNegotiated,
            enrichedParameters.MediaProfile,
            reasonCode,
            violatesPolicy);

        if (violatesPolicy)
        {
            _logger.LogWarning(
                "SRTP policy violation for call {CallId}: policy={Policy} profile={Profile} reason={ReasonCode}",
                session.CallId,
                _appliedSrtpPolicy,
                enrichedParameters.MediaProfile,
                reasonCode);
            return MediaPublicationResult.PolicyViolation;
        }

        _logger.LogDebug(
            "Firing MediaParametersNegotiated for call {CallId}: local={Local} remote={Remote} PT={PT}",
            session.CallId, enrichedParameters.LocalEndPoint, enrichedParameters.RemoteEndPoint, enrichedParameters.PayloadType);

        // Release the port-reservation sockets so the audio and video RtpSessions can bind
        // the same ports.
        _releasePortReservationSockets();

        _lastPublishedSignature = RekeySignature(enrichedParameters);
        _fireNegotiated(enrichedParameters);
        return MediaPublicationResult.Published;
    }

    /// <summary>
    /// Re-publishes media parameters when an established call's re-INVITE changes the negotiated
    /// media (RFC 3264 §8: new SDES key, remote endpoint, or codec). Additive to the initial
    /// <see cref="TryPublish"/>: it runs only after the first publish and only on a real change (a
    /// retransmission with an identical signature is ignored), so the orchestrator rebuilds the media
    /// session with the new keys only when something actually changed.
    /// </summary>
    public void TryRepublishOnRekey(ISipCallSession session)
    {
        var remoteSdp = session.RemoteSdp;
        if (string.IsNullOrWhiteSpace(remoteSdp))
            return;

        var ctx = _contextFactory(session);
        var parameters = _sdpNegotiator.TryParseMediaParameters(remoteSdp, ctx.LocalEndPoint, ctx.SdpOptions);
        if (parameters is null)
            return;

        var withIceMetadata = CallMediaParametersIceEnricher.Enrich(parameters, ctx.LocalIceDescription, ctx.IceControlling);
        var reasonCode = SrtpPolicyEvaluator.ResolveReasonCode(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        // On an inbound re-INVITE the session carries the fresh answer we sent (new local key);
        // on the outbound leg it is null and we fall back to our retained answer/offer.
        var localSdp = session.LocalSdp ?? ctx.LocalAnswerOrOfferSdp;
        var enriched = CallMediaParametersDtlsEnricher.Enrich(
            CallMediaParametersSrtpEnricher.Enrich(
                withIceMetadata, reasonCode, remoteSdp, localSdp, _appliedSrtpPolicy),
            remoteSdp, localSdp);

        if (string.Equals(RekeySignature(enriched), _lastPublishedSignature, StringComparison.Ordinal))
            return; // unchanged — retransmission or a re-INVITE that did not touch media

        var violatesPolicy = SrtpPolicyEvaluator.IsPolicyViolation(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        if (IsKeylessSecureNegotiation(enriched))
        {
            // See TryPublish: a re-INVITE downgrading to a keyless secure
            // negotiation must not slip past the policy either.
            reasonCode = SrtpDecisionReasonCodes.RequiredNegotiationFailed;
            violatesPolicy = _appliedSrtpPolicy == SrtpPolicy.Required;
        }
        _srtpTelemetry.PublishDecision(session, enriched.IsSrtpNegotiated, enriched.MediaProfile, reasonCode, violatesPolicy);
        if (violatesPolicy)
        {
            _ = _terminateForPolicyViolation(session, reasonCode);
            return;
        }

        _activeLocalSrtpKeyParams = enriched.SrtpLocalKeyParams;
        _activeLocalVideoSrtpKeyParams = enriched.Video?.SrtpLocalKeyParams;
        _dtlsActiveOnCall = enriched.IsDtlsNegotiated;
        _lastPublishedSignature = RekeySignature(enriched);
        _logger.LogDebug("Re-publishing media parameters on re-INVITE rekey for call {CallId}.", session.CallId);
        _fireNegotiated(enriched);
    }

    /// <summary>
    /// True when the SDP exchange signals secure media but negotiated no usable keying:
    /// neither SDES key material nor a DTLS association. Such a leg must never run as
    /// plain RTP while reporting <c>IsSrtpNegotiated</c>.
    /// </summary>
    private static bool IsKeylessSecureNegotiation(CallMediaParameters p) =>
        p.IsSrtpNegotiated && p.SrtpLocalKeyParams is null && !p.IsDtlsNegotiated;

    /// <summary>Signature of the media-relevant parameters; equal signature = same media.</summary>
    private static string RekeySignature(CallMediaParameters p) =>
        $"{p.RemoteEndPoint}|{p.PayloadType}|{p.CodecName}|{p.MediaProfile}|{p.IsSrtpNegotiated}"
        + $"|{p.SrtpSuite}|{p.SrtpLocalKeyParams}|{p.SrtpRemoteKeyParams}"
        + $"|{p.IsDtlsNegotiated}|{p.DtlsIsClient}|{p.DtlsRemoteFingerprintAlgorithm}|{p.DtlsRemoteFingerprintValue}";
}
