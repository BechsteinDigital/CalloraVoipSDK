using System.Collections.Concurrent;
using System.Net;
using CalloraVoipSdk.Core.Application.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// The signalling-service behaviour an inbound-INVITE acceptance needs: answering the request, the correlation
/// and trace identities it stamps on telemetry, the lifecycle hook a new session gets, and the event that hands
/// the session to the application. Passed as one record so the acceptance owns the decision sequence without
/// owning the service's own plumbing.
/// </summary>
/// <param name="SendIngressResponseAsync">Answers the request through its server transaction and returns.</param>
/// <param name="ResolveTraceId">The trace id for this Call-ID, created if this is the first sight of it.</param>
/// <param name="BuildCorrelationId">Builds the per-transaction correlation id for telemetry.</param>
/// <param name="HookSessionLifecycle">Subscribes the service to a newly created session's lifecycle.</param>
/// <param name="RaiseIncomingInvite">Hands the created session to the application.</param>
internal sealed record SipInboundInviteAcceptanceHost(
    Func<SipRequest, IPEndPoint, SipTransportProtocol, int, string, Task> SendIngressResponseAsync,
    Func<string, string> ResolveTraceId,
    Func<string, string, string, string> BuildCorrelationId,
    Action<SipCallSession> HookSessionLifecycle,
    Action<SipCallSession> RaiseIncomingInvite);

/// <summary>
/// Accepts a brand-new inbound INVITE: the gates that can turn it away, and the session creation and
/// announcement past them (RFC 3261 §8.2.2.1, §21.4.24; RFC 3891 for <c>Replaces</c>).
/// </summary>
/// <remarks>
/// Extracted from <see cref="SipCallSignalingService"/>, whose <c>HandleInboundRequest</c> had grown to 367
/// lines — by a wide margin the largest block in the SIP tree. The seam is where the method stops dispatching by
/// method name and starts building a dialog: everything before it decides <em>whether</em> to look at the
/// request at all, everything here decides whether this endpoint will take the call.
/// </remarks>
internal sealed class SipInboundInviteAcceptance
{
    private readonly ConcurrentDictionary<string, SipCallSession> _sessions;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessionStartTimes;
    private readonly ConcurrentDictionary<string, string> _sessionTraceIds;
    private readonly ConcurrentDictionary<string, string> _replacementTargets;
    private readonly ISipUasUserIdentityPolicy _userIdentityPolicy;
    private readonly SipInboundSessionAdmission _admission;
    private readonly SipInboundRingDeadlineMonitor _ringDeadline;
    private readonly ISipTelemetrySink _telemetry;
    private readonly SipCallSessionDependencies _sessionDependencies;
    private readonly string _inboundUserAgent;
    private readonly TimeSpan _sessionTimeout;
    private readonly ILogger _logger;
    private readonly SipInboundInviteAcceptanceHost _host;

    /// <param name="sessions">The service's live session map, shared: the Replaces lookup and the insert both use it.</param>
    /// <param name="sessionStartTimes">Per-Call-ID start instants for telemetry.</param>
    /// <param name="sessionTraceIds">Per-Call-ID trace ids for telemetry.</param>
    /// <param name="replacementTargets">Records which dialog a <c>Replaces</c> INVITE is replacing.</param>
    /// <param name="userIdentityPolicy">Decides whether the Request-URI addresses a user this UAS serves.</param>
    /// <param name="admission">The concurrent-inbound-session ceiling, claimed before any dialog state exists.</param>
    /// <param name="ringDeadline">Bounds how long the new session may sit un-answered.</param>
    /// <param name="telemetry">Receives the inbound-INVITE event.</param>
    /// <param name="sessionDependencies">The collaborators a created session needs.</param>
    /// <param name="inboundUserAgent">The User-Agent this endpoint presents on the inbound leg.</param>
    /// <param name="sessionTimeout">The created session's transaction timeout.</param>
    /// <param name="logger">The owning service's logger.</param>
    /// <param name="host">The service behaviour this acceptance drives.</param>
    public SipInboundInviteAcceptance(
        ConcurrentDictionary<string, SipCallSession> sessions,
        ConcurrentDictionary<string, DateTimeOffset> sessionStartTimes,
        ConcurrentDictionary<string, string> sessionTraceIds,
        ConcurrentDictionary<string, string> replacementTargets,
        ISipUasUserIdentityPolicy userIdentityPolicy,
        SipInboundSessionAdmission admission,
        SipInboundRingDeadlineMonitor ringDeadline,
        ISipTelemetrySink telemetry,
        SipCallSessionDependencies sessionDependencies,
        string inboundUserAgent,
        TimeSpan sessionTimeout,
        ILogger logger,
        SipInboundInviteAcceptanceHost host)
    {
        _sessions = sessions;
        _sessionStartTimes = sessionStartTimes;
        _sessionTraceIds = sessionTraceIds;
        _replacementTargets = replacementTargets;
        _userIdentityPolicy = userIdentityPolicy;
        _admission = admission;
        _ringDeadline = ringDeadline;
        _telemetry = telemetry;
        _sessionDependencies = sessionDependencies;
        _inboundUserAgent = inboundUserAgent;
        _sessionTimeout = sessionTimeout;
        _logger = logger;
        _host = host;
    }

    /// <summary>
    /// Accepts a brand-new inbound INVITE — one that opens a dialog rather than belonging to an existing one —
    /// and returns once the session is created and announced, or once the request has been answered and dropped.
    /// </summary>
    /// <remarks>
    /// The order is a sequence of gates, each of which can end the request: an in-dialog To-tag (not ours),
    /// <c>Replaces</c> that does not name a dialog we hold (481), a user this UAS does not serve (404,
    /// RFC 3261 §8.2.2.1), and the concurrent-session ceiling (486). Only past all of them is dialog state
    /// created — which is the point of the ordering: a flood of INVITEs must be turned away before it can pin
    /// state, not after.
    /// </remarks>
    /// <param name="normalizedRequest">The validated inbound INVITE.</param>
    /// <param name="callId">Its Call-ID, already extracted.</param>
    /// <param name="remoteEndPoint">The peer's address, taken from the connection it arrived on.</param>
    /// <param name="inboundTransport">The transport it arrived on — never reconstructed from the Via.</param>
    public void Accept(
        SipRequest normalizedRequest,
        string callId,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol inboundTransport)
    {
        var toTag = SipProtocol.ExtractTag(normalizedRequest.Header("To"));
        if (!string.IsNullOrWhiteSpace(toTag))
            return;

        string? replacesTargetCallId = null;
        var replacesHeader = normalizedRequest.Header("Replaces");
        if (!string.IsNullOrWhiteSpace(replacesHeader))
        {
            if (!SipReplacesHeaderValue.TryParse(replacesHeader, out var replaces))
            {
                _ = _host.SendIngressResponseAsync(
                    normalizedRequest,
                    remoteEndPoint,
                    inboundTransport,
                    /* statusCode: */ 400,
                    /* reasonPhrase: */ "Bad Request");
                return;
            }

            if (!_sessions.TryGetValue(replaces!.CallId, out var replacesTargetSession)
                || !replacesTargetSession.MatchesReplacesTarget(replaces))
            {
                _ = _host.SendIngressResponseAsync(
                    normalizedRequest,
                    remoteEndPoint,
                    inboundTransport,
                    /* statusCode: */ 481,
                    /* reasonPhrase: */ "Call/Transaction Does Not Exist");
                return;
            }

            replacesTargetCallId = replaces.CallId;
        }

        var remoteUri = SipProtocol.ExtractUriFromNameAddr(normalizedRequest.Header("From"));
        var toUri = SipProtocol.ExtractUriFromNameAddr(normalizedRequest.Header("To"));
        if (string.IsNullOrWhiteSpace(remoteUri) || string.IsNullOrWhiteSpace(toUri))
            return;

        // DESIGN DECISION (#13): inbound requests are authorised by served-user + identity-trust/peer matching
        // (trunk IP, TrustedRegistrarAddresses), NOT by issuing a 401/407 digest challenge to the peer. This suits
        // the trusted-trunk / peered-registrar deployment model. Adding UAS-side digest challenge of inbound
        // requests is a deliberate, separate feature decision, not an oversight.
        // RFC 3261 §8.2.2.1: Reject INVITE to unknown users with 404 Not Found.
        if (!_userIdentityPolicy.IsServedUser(normalizedRequest.RequestUri))
        {
            _ = _host.SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                /* statusCode: */ 404,
                /* reasonPhrase: */ "Not Found");
            return;
        }

        // #158 P1-5: bound the number of concurrent inbound sessions before creating dialog state. A UAS
        // creates a session (and fires IncomingInvite) for every served-user INVITE, before any line/trunk
        // takes ownership — a flood of INVITEs with distinct Call-IDs would otherwise pin unbounded state.
        // At the cap, answer 486 Busy Here (RFC 3261 §21.4.24) and create no session. The slot is claimed here
        // and released on every path that does not end in a tracked session (#279) — reading _sessions.Count
        // instead would let concurrent INVITEs all observe the same free slot and admit past the ceiling.
        var admission = _admission.TryAdmitInbound(callId, remoteEndPoint.Address);
        if (admission != SipInboundSessionAdmissionOutcome.Admitted)
        {
            _logger.LogWarning(
                "Inbound INVITE from {Remote} rejected by session admission ({Outcome}); ceiling is {Cap} " +
                "concurrent inbound sessions.",
                remoteEndPoint, admission, _admission.MaxConcurrentSessions);
            _ = _host.SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                /* statusCode: */ 486,
                /* reasonPhrase: */ "Busy Here");
            return;
        }

        string localTag;
        SipCallSession session;
        var sessionSlotCommitted = false;
        try
        {
            localTag = SipProtocol.NewTag();
            var configuration = new SipCallSessionConfiguration
            {
                CallId = callId,
                LocalUri = toUri,
                RemoteUri = remoteUri,
                LocalDisplayName = null,
                PreferredIdentityUri = null,
                AuthUsername = string.Empty,
                AuthPassword = null,
                UserAgent = _inboundUserAgent,
                Timeout = _sessionTimeout,
                RemoteEndPoint = remoteEndPoint,
                SignalingTransport = inboundTransport
            };
            var inboundContext = new SipInboundSessionContext
            {
                InitialInvite = normalizedRequest,
                LocalTag = localTag
            };

            session = SipCallSession.CreateInbound(
                configuration,
                inboundContext,
                _sessionDependencies);

            if (!_sessions.TryAdd(callId, session))
            {
                session.Dispose();
                return;
            }

            sessionSlotCommitted = true;
        }
        finally
        {
            // #279: the reservation covers a tracked session only once the insert succeeded. Release it on
            // every other exit — a duplicate Call-ID or a throw out of session construction — or the ceiling
            // shrinks with each of them until the service stops admitting calls.
            if (!sessionSlotCommitted)
                _admission.ReleaseInbound(callId);
        }

        var traceId = _host.ResolveTraceId(callId);
        if (!string.IsNullOrWhiteSpace(replacesTargetCallId))
            _replacementTargets[callId] = replacesTargetCallId;

        _host.HookSessionLifecycle(session);
        _sessionStartTimes[callId] = DateTimeOffset.UtcNow;
        _sessionTraceIds[callId] = traceId;
        // #158 P1-5 (ring deadline): bound how long this session may sit in Ringing without an answer. Started
        // before IncomingInvite so a consumer that answers/rejects synchronously cancels it via the lifecycle
        // hook below; on expiry the monitor rejects 480, which drives the session to Terminated and cleanup.
        _ringDeadline.Track(session);
        var inboundAttributes = new Dictionary<string, string>
        {
            ["remote_uri"] = remoteUri,
            ["local_uri"] = toUri
        };
        if (!string.IsNullOrWhiteSpace(session.RemoteAssertedIdentity))
            inboundAttributes["remote_asserted_identity"] = session.RemoteAssertedIdentity!;
        if (!string.IsNullOrWhiteSpace(replacesTargetCallId))
            inboundAttributes["replaces_call_id"] = replacesTargetCallId;

        _telemetry.PublishEvent(new SipEventRecord
        {
            EventType = "sip.dialog.inbound_invite.received",
            CallId = callId,
            CorrelationId = _host.BuildCorrelationId(callId, "INVITE", localTag),
            TraceId = traceId,
            Attributes = inboundAttributes
        });
        _host.RaiseIncomingInvite(session);
    }
}
