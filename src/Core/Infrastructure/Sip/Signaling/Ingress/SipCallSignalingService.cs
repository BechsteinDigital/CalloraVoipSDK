using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using static CalloraVoipSdk.Core.Infrastructure.Sip.Signaling.SipCallSignalingHelpers;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Default SIP call signaling core service.
/// Handles outbound INVITE flows and inbound INVITE dispatch to call sessions.
/// </summary>
internal sealed class SipCallSignalingService : ISipCallSignalingService
{
    private const string SupportedMethodList = "INVITE, ACK, BYE, CANCEL, OPTIONS, INFO, REFER, NOTIFY, UPDATE, PRACK, SUBSCRIBE, MESSAGE";
    private const string SupportedAcceptList = "application/sdp, application/dtmf-relay, message/sipfrag";

    private const string DefaultInboundUserAgent = "CalloraVoipSdk/1.0";
    private static readonly TimeSpan DefaultInboundSessionTimeout = TimeSpan.FromSeconds(30);

    // Upper bound on distinct outbound-INVITE targets (initial + all 3xx redirects) so a 3xx carrying many
    // Contacts cannot fan out into an unbounded chain of INVITE transactions (RFC 3261 §8.1.3.4 hardening).
    private const int MaxRedirectTargets = 8;

    private readonly string _inboundUserAgent;
    private readonly ISipTransportRuntime _transport;
    private readonly ISipDigestAuthenticator _digestAuthenticator;
    private readonly ISipServerTransactionEngine _serverTransactions;
    private readonly ISipIdentityTrustPolicy _identityTrustPolicy;
    private readonly ISipUasUserIdentityPolicy _userIdentityPolicy;
    private readonly ISipTelemetrySink _telemetry;
    private readonly SipCallSessionDependencies _sessionDependencies;
    private readonly ILogger<SipCallSignalingService> _logger;
    private readonly SipClientTransactionExecutor _subscribeExecutor;
    private readonly SipCallSignalingSubscriptions _subscriptionService;
    private readonly SipCallSignalingMessages _messageService;
    private readonly SipCallSignalingPublications _publicationService;
    private readonly ConcurrentDictionary<string, SipCallSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SipOutboundSubscriptionEntry> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessionStartTimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionTraceIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _replacementTargets = new(StringComparer.Ordinal);
    private readonly SipMergedInviteTracker _mergedInviteTracker = new();
    private readonly SipReplacedDialogTerminator _replacedDialogTerminator;
    private readonly IDisposable _requestSubscription;
    private readonly IDisposable _responseSubscription;
    private readonly SipInboundRingDeadlineMonitor _ringDeadline;

    /// <summary>
    /// Reserves the slot behind every entry in <see cref="_sessions"/> (#158 P1-5, #279): inbound sessions are
    /// admitted against the global and per-remote ceilings before construction, outbound ones take their slot
    /// unconditionally. Each reservation is released exactly once by <see cref="TryUntrackSession"/> or by the
    /// admission path that failed to reach the table.
    /// </summary>
    private readonly SipInboundSessionAdmission _admission;
    private int _disposed;

    /// <summary>
    /// Creates call signaling service and subscribes to transport events.
    /// </summary>
    public SipCallSignalingService(
        ISipTransportRuntime transport,
        ISipDigestAuthenticator digestAuthenticator,
        ILoggerFactory loggerFactory,
        SipSessionSdpProvider? sdpProvider = null,
        ISipTelemetrySink? telemetry = null,
        ISipIdentityTrustPolicy? identityTrustPolicy = null,
        ISipUasUserIdentityPolicy? userIdentityPolicy = null,
        string? inboundUserAgent = null,
        int? maxConcurrentInboundSessions = null,
        TimeSpan? inboundRingDeadline = null,
        int? maxInboundSessionsPerRemote = null,
        int? maxServerTransactions = null,
        TimeSpan? absoluteServerTransactionLifetime = null)
    {
        var resolvedDigestAuthenticator = digestAuthenticator
            ?? throw new ArgumentNullException(nameof(digestAuthenticator));
        _admission = new SipInboundSessionAdmission(maxConcurrentInboundSessions, maxInboundSessionsPerRemote);
        _inboundUserAgent = string.IsNullOrWhiteSpace(inboundUserAgent) ? DefaultInboundUserAgent : inboundUserAgent;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _digestAuthenticator = resolvedDigestAuthenticator;
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<SipCallSignalingService>();
        _telemetry = telemetry ?? NullSipTelemetrySink.Instance;
        _replacedDialogTerminator = new SipReplacedDialogTerminator(_telemetry, _logger);
        _identityTrustPolicy = identityTrustPolicy ?? DenyAllSipIdentityTrustPolicy.Instance;
        _userIdentityPolicy = userIdentityPolicy ?? AcceptAllSipUasUserIdentityPolicy.Instance;
        _serverTransactions = new SipServerTransactionEngine(
            _transport, _logger, maxServerTransactions, absoluteServerTransactionLifetime);
        _subscribeExecutor = new SipClientTransactionExecutor(_transport, _logger);
        _subscriptionService = new SipCallSignalingSubscriptions(
            _transport,
            _digestAuthenticator,
            _subscribeExecutor,
            _subscriptions,
            _logger,
            SendIngressResponseAsync);
        _messageService = new SipCallSignalingMessages(_transport, _digestAuthenticator, _subscribeExecutor, _logger);
        _publicationService = new SipCallSignalingPublications(_transport, _digestAuthenticator, _subscribeExecutor, _logger);
        _ringDeadline = new SipInboundRingDeadlineMonitor(_logger, inboundRingDeadline);

        var resolvedSdpProvider = sdpProvider ?? BuildDefaultSdpProvider();
        _sessionDependencies = new SipCallSessionDependencies
        {
            Transport = _transport,
            DigestAuthenticator = resolvedDigestAuthenticator,
            Logger = _logger,
            ServerTransactions = _serverTransactions,
            IdentityTrustPolicy = _identityTrustPolicy,
            SdpProvider = resolvedSdpProvider,
        };

        _requestSubscription = _transport.SubscribeRequests(HandleInboundRequest);
        _responseSubscription = _transport.SubscribeResponses(HandleInboundResponse);
    }

    /// <inheritdoc />
    public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite;

    /// <inheritdoc />
    public event EventHandler<SipIncomingMessageEventArgs>? IncomingMessage;

    /// <inheritdoc />
    public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted;

    /// <inheritdoc />
    public async Task<ISipCallSession> InviteAsync(
        SipInviteRequest request,
        Action<ISipCallSession>? onSessionCreated = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateInviteRequest(request);

        var normalizedRemoteUri = SipProtocol.ExtractUriFromNameAddr(request.RemoteUri);
        if (!SipProtocol.TryParseSipUri(normalizedRemoteUri, out _, out _, out _))
            throw new ArgumentException($"RemoteUri must be a valid SIP URI, got '{request.RemoteUri}'.", nameof(request));

        // An IP-authenticated trunk has no account user, and "sip:@domain" is not a SIP URI — RFC 3261
        // §19.1.1 makes the userinfo part optional, so the address is then simply the host.
        var localUri = string.IsNullOrWhiteSpace(request.LocalUsername)
            ? $"sip:{request.LocalDomain}"
            : $"sip:{request.LocalUsername}@{request.LocalDomain}";
        var callId = SipProtocol.NewCallId();
        var localTag = SipProtocol.NewTag();
        var traceId = ResolveTraceId(callId);
        var authUser = string.IsNullOrWhiteSpace(request.AuthUsername)
            ? request.LocalUsername
            : request.AuthUsername;

        _telemetry.PublishEvent(new SipEventRecord
        {
            EventType = "sip.dialog.outbound_invite.started",
            CallId = callId,
            CorrelationId = BuildCorrelationId(callId, "INVITE", localTag),
            TraceId = traceId,
            Attributes = new Dictionary<string, string>
            {
                ["remote_uri"] = request.RemoteUri,
                ["transport"] = request.Transport.ToString()
            }
        });

        var initialTarget = SipInitialRequestRoutingPlanner.CreateInitialTarget(
            normalizedRemoteUri,
            request.PreloadedRouteSet);
        var pendingTargets = new Queue<SipOutboundInviteTarget>();
        var visitedRequestUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pendingTargets.Enqueue(initialTarget);
        visitedRequestUris.Add(initialTarget.RequestUri);
        var effectiveSessionDescription = request.SessionDescription;
        var effectiveRequireHeader = request.RequireHeader;
        var effectiveProxyRequireHeader = request.ProxyRequireHeader;
        var reducedBodyRetryUsed = false;
        Exception? lastFailure = null;

        while (pendingTargets.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var target = pendingTargets.Dequeue();
            if (!SipProtocol.TryParseSipUri(target.NextHopUri, out _, out var targetHost, out var targetPortFromUri))
                continue;

            var secureTarget = SipProtocol.IsSipsUri(target.NextHopUri);
            if (secureTarget
                && request.Transport is not SipTransportProtocol.Tls and not SipTransportProtocol.Wss)
            {
                throw new ArgumentException(
                    "SIPS targets require TLS-capable transport (TLS or WSS).",
                    nameof(request));
            }

            var targetPort = targetPortFromUri
                ?? ResolveDefaultRemotePort(request.RemotePort, secureTarget);
            var routeCandidates = await _transport.ResolveRemoteRouteCandidatesAsync(
                    targetHost,
                    targetPort,
                    request.Transport,
                    ct)
                .ConfigureAwait(false);

            foreach (var routeCandidate in routeCandidates)
            {
                ct.ThrowIfCancellationRequested();
                if (secureTarget
                    && routeCandidate.Transport is not SipTransportProtocol.Tls and not SipTransportProtocol.Wss)
                {
                    continue;
                }

                var configuration = new SipCallSessionConfiguration
                {
                    CallId = callId,
                    LocalUri = localUri,
                    RemoteUri = target.LogicalRemoteUri,
                    InitialRequestUri = target.RequestUri,
                    InitialRouteSet = target.RouteSet,
                    LocalDisplayName = request.LocalDisplayName,
                    PreferredIdentityUri = request.PreferredIdentityUri,
                    PrivacyHeader = request.Privacy,
                    RequireHeader = effectiveRequireHeader,
                    ProxyRequireHeader = effectiveProxyRequireHeader,
                    AuthUsername = authUser ?? request.LocalUsername,
                    AuthPassword = request.AuthPassword,
                    UserAgent = request.UserAgent,
                    Timeout = request.Timeout,
                    RemoteEndPoint = routeCandidate.EndPoint,
                    SignalingTransport = routeCandidate.Transport,
                    ReferredBy = request.ReferredBy,
                    CustomHeaders = request.CustomHeaders,
                    LineTls = request.LineTls
                };

                var session = SipCallSession.CreateOutbound(
                    configuration,
                    _sessionDependencies);
                // #279: an outbound call is never refused by the inbound ceiling, but it does occupy a slot in
                // the same table, so it claims one unconditionally — exactly as the previous _sessions.Count
                // check counted outbound sessions against the cap.
                _admission.ReserveOutbound();
                if (!_sessions.TryAdd(callId, session))
                {
                    _admission.ReleaseSlot();
                    throw new InvalidOperationException($"Session with Call-ID '{callId}' already exists.");
                }
                HookSessionLifecycle(session);
                _sessionStartTimes[callId] = DateTimeOffset.UtcNow;
                _sessionTraceIds[callId] = traceId;

                // Bind the session to its channel now — before the INVITE goes out — so the media
                // adapter observes the early dialog (Ringing/183) live instead of only after 200 OK (F011).
                // A throwing callback (e.g. ObjectDisposedException from AttachSession on a concurrent
                // channel dispose) must not leak the session in _sessions.
                try
                {
                    onSessionCreated?.Invoke(session);
                }
                catch
                {
                    CleanupFailedOutboundSession(callId, session);
                    throw;
                }

                try
                {
                    await session.StartOutboundInviteAsync(
                            effectiveSessionDescription,
                            localTag,
                            ct)
                        .ConfigureAwait(false);
                    // HARD-C3: raise OutboundCallStarted only after the INVITE actually succeeds, and
                    // exactly once. Firing per attempt (before the transaction) dispatched a session
                    // that a redirect/retry then disposes, and fired again for each retry target.
                    OutboundCallStarted?.Invoke(this, new SipIncomingInviteEventArgs(session));
                    return session;
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // Caller cancelled the in-flight INVITE. The transaction layer reports a cancelled wait
                    // as a TimeoutException (the token linked into WaitForFinalResponseAsync), so key off the
                    // token, not the exception type. Unlike the failure paths below, leave the session alive
                    // and channel-bound so the caller's HangupAsync can put a wire-CANCEL on the wire
                    // (RFC 3261 §9.1) and reach Terminated(487); disposing here strands the UAS dialog (the
                    // Asterisk channel stays up) with no CANCEL. The lifecycle hook removes it on Terminated.
                    _logger.LogDebug("Outbound INVITE {CallId} cancelled in flight; keeping the session cancelable.", callId);
                    throw new OperationCanceledException(ct);
                }
                catch (SipFinalResponseException finalResponseEx)
                {
                    CleanupFailedOutboundSession(callId, session);
                    lastFailure = finalResponseEx;
                    var response = finalResponseEx.FinalResponse.Response;
                    if (response.StatusCode is >= 300 and < 400)
                    {
                        SipOutboundInviteRetryPolicy.EnqueueRedirectTargets(
                            response,
                            pendingTargets,
                            visitedRequestUris,
                            MaxRedirectTargets);
                        break;
                    }

                    if (response.StatusCode is 413 or 415)
                    {
                        if (!reducedBodyRetryUsed)
                        {
                            reducedBodyRetryUsed = true;
                            effectiveSessionDescription = string.Empty;
                            pendingTargets.Enqueue(target);
                            break;
                        }
                    }

                    // A 416 (Unsupported URI Scheme) on a sips: target is NOT auto-downgraded to sip: (#158 P1-1):
                    // downgrading would let a peer or proxy strip the caller's end-to-end SIPS security intent down
                    // to a cleartext hop. The 416 propagates as a final failure instead.

                    if (response.StatusCode == 420)
                    {
                        if (SipOutboundInviteRetryPolicy.TryRemoveUnsupportedOptions(
                                response.Header("Unsupported"),
                                effectiveRequireHeader,
                                effectiveProxyRequireHeader,
                                out var nextRequireHeader,
                                out var nextProxyRequireHeader))
                        {
                            effectiveRequireHeader = nextRequireHeader;
                            effectiveProxyRequireHeader = nextProxyRequireHeader;
                            pendingTargets.Enqueue(target);
                            break;
                        }
                    }

                    throw;
                }
                catch (TimeoutException timeoutEx)
                {
                    CleanupFailedOutboundSession(callId, session);
                    _logger.LogDebug(
                        timeoutEx,
                        "SIP INVITE target {TargetUri} at {RemoteEndPoint} timed out for {CallId}.",
                        target.RequestUri,
                        routeCandidate.EndPoint,
                        callId);
                    lastFailure = timeoutEx;
                    continue;
                }
                catch (InvalidOperationException transportEx) when (IsTransportFailure(transportEx))
                {
                    CleanupFailedOutboundSession(callId, session);
                    _logger.LogDebug(
                        transportEx,
                        "SIP INVITE target {TargetUri} at {RemoteEndPoint} failed with transport error for {CallId}.",
                        target.RequestUri,
                        routeCandidate.EndPoint,
                        callId);
                    lastFailure = transportEx;
                    continue;
                }
                catch (Exception ex)
                {
                    CleanupFailedOutboundSession(callId, session);
                    _logger.LogWarning(
                        ex,
                        "Failed to start outbound SIP INVITE session {CallId} to {RemoteUri}.",
                        callId,
                        target.RequestUri);
                    throw;
                }
            }
        }

        if (lastFailure is TimeoutException timeoutFailure)
            throw SipOutboundInviteRetryPolicy.CreateSyntheticTransactionFailure(408, "Request Timeout", callId, timeoutFailure);
        if (lastFailure is InvalidOperationException transportFailure && IsTransportFailure(transportFailure))
            throw SipOutboundInviteRetryPolicy.CreateSyntheticTransactionFailure(503, "Service Unavailable", callId, transportFailure);
        if (lastFailure is not null)
            throw lastFailure;

        throw new InvalidOperationException(
            $"No routable SIP targets remained for outbound INVITE to '{request.RemoteUri}'.");
    }

    /// <summary>
    /// Removes one session from the table and releases its admission slot exactly once (#279). Every removal
    /// path goes through here — removing an entry without releasing its slot would shrink the effective
    /// ceiling until the service stopped admitting calls entirely.
    /// </summary>
    private bool TryUntrackSession(string callId)
    {
        if (!_sessions.TryRemove(callId, out _))
            return false;

        _admission.ReleaseInbound(callId);
        return true;
    }

    /// <summary>
    /// Removes and disposes one failed outbound session attempt.
    /// </summary>
    private void CleanupFailedOutboundSession(string callId, SipCallSession session)
    {
        TryUntrackSession(callId);
        _sessionStartTimes.TryRemove(callId, out _);
        _sessionTraceIds.TryRemove(callId, out _);
        session.Dispose();
    }


    /// <summary>
    /// Handles inbound SIP request dispatch.
    /// </summary>
    private void HandleInboundRequest(SipInboundRequestContext context, SipRequest request)
    {
        if (request is null) return;

        // #158 P1-2: the transport comes from the accepted connection the request actually arrived on — never
        // reconstructed from the peer-controlled Via — and the connection id lets responses go back over that
        // exact connection. Both flow into the server transaction via RegisterInboundRequest below.
        var remoteEndPoint = context.RemoteEndPoint;
        var inboundTransport = context.Transport;
        if (!SipIngressRequestPolicy.TryValidateIngressRequest(request, out var ingressRejectionCode, out var ingressRejectionReasonPhrase))
        {
            if (!string.Equals(request.Method, "ACK", StringComparison.Ordinal))
            {
                _ = SendIngressResponseAsync(
                    request,
                    remoteEndPoint,
                    inboundTransport,
                    ingressRejectionCode,
                    ingressRejectionReasonPhrase);
            }

            return;
        }

        var callId = request.Header("Call-ID");
        if (string.IsNullOrWhiteSpace(callId)) return;
        var registration = _serverTransactions.RegisterInboundRequest(context, request);
        if (!registration.ShouldProcess)
            return;

        if (SipIngressRequestPolicy.IsLoopDetected(request))
        {
            if (!string.Equals(request.Method, "ACK", StringComparison.Ordinal))
            {
                _ = SendIngressResponseAsync(
                    request,
                    remoteEndPoint,
                    inboundTransport,
                    statusCode: 482,
                    reasonPhrase: "Loop Detected");
            }

            return;
        }

        if (!SipIngressRequestPolicy.TryValidateMaxForwards(request, out var rejectionCode, out var rejectionReasonPhrase))
        {
            if (!string.Equals(request.Method, "ACK", StringComparison.Ordinal))
            {
                _ = SendIngressResponseAsync(
                    request,
                    remoteEndPoint,
                    inboundTransport,
                    rejectionCode,
                    rejectionReasonPhrase);
            }

            return;
        }

        // A UAS does not decrement Max-Forwards — that is a proxy responsibility (RFC 3261 §16.6). No ingress
        // normalization is applied here; the alias keeps the seam for any future inbound request normalization.
        var normalizedRequest = request;

        if (!string.Equals(normalizedRequest.Method, "ACK", StringComparison.Ordinal)
            && !string.Equals(normalizedRequest.Method, "CANCEL", StringComparison.Ordinal)
            && !SipRequireOptionPolicy.TryValidateRequestRequireHeader(
                normalizedRequest,
                out var unsupportedHeaderValue))
        {
            var unsupportedHeaders = SipIngressResponseHeaders.Create(normalizedRequest, statusCode: 420);
            unsupportedHeaders["Unsupported"] = unsupportedHeaderValue;
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 420,
                reasonPhrase: "Bad Extension",
                unsupportedHeaders);
            return;
        }

        if (string.Equals(normalizedRequest.Method, "INVITE", StringComparison.Ordinal)
            && !SipContentPolicy.TryValidateSdpRequest(
                normalizedRequest,
                out var contentRejectionStatusCode,
                out var contentRejectionReasonPhrase,
                out var contentRejectionHeaders))
        {
            var rejectionHeaders = SipIngressResponseHeaders.Create(normalizedRequest, contentRejectionStatusCode);
            if (contentRejectionHeaders is not null)
            {
                foreach (var pair in contentRejectionHeaders)
                    rejectionHeaders[pair.Key] = pair.Value;
            }

            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                contentRejectionStatusCode,
                contentRejectionReasonPhrase,
                rejectionHeaders);
            return;
        }

        if (string.Equals(normalizedRequest.Method, "INVITE", StringComparison.Ordinal)
            && _mergedInviteTracker.IsMergedInvite(normalizedRequest))
        {
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 482,
                reasonPhrase: "Loop Detected");
            return;
        }

        if (string.Equals(normalizedRequest.Method, "INVITE", StringComparison.Ordinal))
        {
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 100,
                reasonPhrase: "Trying");
        }

        if (_sessions.TryGetValue(callId, out var existing))
        {
            _ = existing.HandleInboundRequestAsync(remoteEndPoint, normalizedRequest, CancellationToken.None);
            return;
        }

        if (string.Equals(normalizedRequest.Method, "CANCEL", StringComparison.Ordinal))
        {
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 481,
                reasonPhrase: "Call/Transaction Does Not Exist");
            return;
        }

        if (string.Equals(normalizedRequest.Method, "OPTIONS", StringComparison.Ordinal))
        {
            var headers = SipIngressResponseHeaders.Create(normalizedRequest, statusCode: 200);
            headers["Allow"] = SupportedMethodList;
            headers["Accept"] = SupportedAcceptList;
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 200,
                reasonPhrase: "OK",
                headers);
            return;
        }

        // RFC 6665 §6.1.1: out-of-dialog NOTIFY for active subscriptions.
        if (string.Equals(normalizedRequest.Method, "NOTIFY", StringComparison.Ordinal)
            && _subscriptions.TryGetValue(callId, out var outboundSubscription))
        {
            _subscriptionService.HandleInboundSubscriptionNotify(remoteEndPoint, normalizedRequest, inboundTransport, outboundSubscription);
            return;
        }

        // RFC 3428 §7: a MESSAGE is a pager-mode instant message that opens no dialog. Answer it 200 OK
        // and surface its content to the application via IncomingMessage (the request creates no session).
        if (string.Equals(normalizedRequest.Method, "MESSAGE", StringComparison.Ordinal))
        {
            IncomingMessage?.Invoke(this, SipIncomingMessageEventArgs.FromRequest(normalizedRequest, callId, remoteEndPoint));
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 200,
                reasonPhrase: "OK");
            return;
        }

        if (IsDialogScopedMethod(normalizedRequest.Method))
        {
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 481,
                reasonPhrase: "Call/Transaction Does Not Exist");
            return;
        }

        if (!string.Equals(normalizedRequest.Method, "INVITE", StringComparison.Ordinal)
            && !string.Equals(normalizedRequest.Method, "ACK", StringComparison.Ordinal))
        {
            var headers = SipIngressResponseHeaders.Create(normalizedRequest, statusCode: 501);
            headers["Allow"] = SupportedMethodList;
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 501,
                reasonPhrase: "Not Implemented",
                headers);
            return;
        }

        if (!string.Equals(normalizedRequest.Method, "INVITE", StringComparison.Ordinal))
            return;

        var toTag = SipProtocol.ExtractTag(normalizedRequest.Header("To"));
        if (!string.IsNullOrWhiteSpace(toTag))
            return;

        string? replacesTargetCallId = null;
        var replacesHeader = normalizedRequest.Header("Replaces");
        if (!string.IsNullOrWhiteSpace(replacesHeader))
        {
            if (!SipReplacesHeaderValue.TryParse(replacesHeader, out var replaces))
            {
                _ = SendIngressResponseAsync(
                    normalizedRequest,
                    remoteEndPoint,
                    inboundTransport,
                    statusCode: 400,
                    reasonPhrase: "Bad Request");
                return;
            }

            if (!_sessions.TryGetValue(replaces!.CallId, out var replacesTargetSession)
                || !replacesTargetSession.MatchesReplacesTarget(replaces))
            {
                _ = SendIngressResponseAsync(
                    normalizedRequest,
                    remoteEndPoint,
                    inboundTransport,
                    statusCode: 481,
                    reasonPhrase: "Call/Transaction Does Not Exist");
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
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 404,
                reasonPhrase: "Not Found");
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
            _ = SendIngressResponseAsync(
                normalizedRequest,
                remoteEndPoint,
                inboundTransport,
                statusCode: 486,
                reasonPhrase: "Busy Here");
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
                Timeout = DefaultInboundSessionTimeout,
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

        var traceId = ResolveTraceId(callId);
        if (!string.IsNullOrWhiteSpace(replacesTargetCallId))
            _replacementTargets[callId] = replacesTargetCallId;

        HookSessionLifecycle(session);
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
            CorrelationId = BuildCorrelationId(callId, "INVITE", localTag),
            TraceId = traceId,
            Attributes = inboundAttributes
        });
        IncomingInvite?.Invoke(this, new SipIncomingInviteEventArgs(session));
    }

    /// <summary>
    /// Handles inbound SIP response dispatch.
    /// </summary>
    private void HandleInboundResponse(IPEndPoint remoteEndPoint, SipResponse response)
    {
        if (response is null) return;
        var callId = response.Header("Call-ID");
        if (string.IsNullOrWhiteSpace(callId)) return;
        if (!_sessions.TryGetValue(callId, out var session)) return;
        session.HandleInboundResponse(remoteEndPoint, response);
    }

    /// <summary>
    /// Hooks lifecycle handlers to remove terminated sessions from dictionary.
    /// </summary>
    private void HookSessionLifecycle(SipCallSession session)
    {
        session.StateChanged += (_, e) =>
        {
            // #158 P1-5 (ring deadline): once the session leaves Ringing (answered, rejected, or terminated)
            // it is no longer stale — cancel any pending ring-deadline timer. No-op for outbound sessions,
            // which are never tracked.
            if (e.NewState != SipDialogState.Ringing)
                _ringDeadline.Cancel(session.CallId);

            var traceId = _sessionTraceIds.TryGetValue(session.CallId, out var activeTraceId)
                ? activeTraceId
                : ResolveTraceId(session.CallId);
            var attributes = new Dictionary<string, string>
            {
                ["old_state"] = e.OldState.ToString(),
                ["new_state"] = e.NewState.ToString()
            };
            if (e.NewState == SipDialogState.Terminated
                && e.TerminationReason is not null)
            {
                attributes["reason.protocol"] = e.TerminationReason.Protocol;
                if (e.TerminationReason.Cause is { } cause)
                    attributes["reason.cause"] = cause.ToString();
                if (!string.IsNullOrWhiteSpace(e.TerminationReason.Text))
                    attributes["reason.text"] = e.TerminationReason.Text!;
            }

            _telemetry.PublishEvent(new SipEventRecord
            {
                EventType = "sip.dialog.state.changed",
                CallId = session.CallId,
                CorrelationId = BuildCorrelationId(session.CallId, "STATE", null),
                TraceId = traceId,
                Attributes = attributes
            });

            if (e.NewState == SipDialogState.Established
                && _replacementTargets.TryRemove(session.CallId, out var replacedCallId)
                && _sessions.TryGetValue(replacedCallId, out var replacedSession))
            {
                _ = _replacedDialogTerminator.TerminateAsync(
                    replacedSession, session.CallId, replacedCallId, traceId);
            }

            if (e.NewState != SipDialogState.Terminated) return;
            // Releases the admission slot and the per-remote reservation taken at admission (#158 P1-5, #279);
            // both are no-ops for outbound sessions, which never went through the limiter.
            TryUntrackSession(session.CallId);
            if (_sessionStartTimes.TryRemove(session.CallId, out var startedAt))
            {
                _telemetry.PublishCdr(new SipCdrRecord
                {
                    CallId = session.CallId,
                    LocalUri = session.LocalUri,
                    RemoteUri = session.RemoteUri,
                    StartedAt = startedAt,
                    EndedAt = DateTimeOffset.UtcNow,
                    Outcome = "terminated",
                    TraceId = traceId
                });
            }
            _sessionTraceIds.TryRemove(session.CallId, out var _);
            _replacementTargets.TryRemove(session.CallId, out var _);

            session.Dispose();
        };
    }

    /// <summary>
    /// Terminates one dialog that was targeted by an accepted Replaces INVITE.
    /// </summary>
    /// <inheritdoc />
    public Task<SipSubscriptionHandle> SubscribeAsync(
        SipSubscribeRequest request,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _subscriptionService.SubscribeAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<int> SendMessageAsync(SipMessageRequest request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _messageService.SendMessageAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<SipPublishResult> PublishAsync(SipPublishRequest request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _publicationService.PublishAsync(request, ct);
    }

    /// <summary>
    /// Builds a default <see cref="SipSessionSdpProvider"/> backed by the built-in
    /// <see cref="SdpNegotiator"/>. Used when no explicit provider is supplied
    /// (primarily in unit tests that don't exercise the SDP path).
    /// </summary>
    private static SipSessionSdpProvider BuildDefaultSdpProvider()
    {
        var neg = new Sdp.SdpNegotiator();
        return new SipSessionSdpProvider
        {
            BuildOffer              = (ep, hold) => neg.BuildDefaultSdp(ep, hold, null),
            TryNegotiateAnswer      = (offer, ep, hold) =>
                offer is null ? null : neg.TryBuildNegotiatedAnswer(offer, ep, hold, null),
            TryParseMediaParameters = neg.TryParseMediaParameters,
            IsRemoteHold            = neg.IsRemoteHoldSdp,
        };
    }

    /// <summary>
    /// Disposes subscriptions and active sessions.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _requestSubscription.Dispose();
        _responseSubscription.Dispose();
        _ringDeadline.Dispose();
        _serverTransactions.Dispose();
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        _admission.Clear();
        _sessionStartTimes.Clear();
        _sessionTraceIds.Clear();
        _replacementTargets.Clear();
        foreach (var sub in _subscriptions.Values)
            sub.RefreshCts.Cancel();
        _subscriptions.Clear();
    }

    /// <summary>
    /// Throws if service was already disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipCallSignalingService));
    }

    /// <summary>
    /// Sends one ingress-level SIP response for early validation and provisional handling.
    /// </summary>
    private async Task SendIngressResponseAsync(
        SipRequest request,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        try
        {
            await _serverTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    transport,
                    statusCode,
                    reasonPhrase,
                    headers is null
                        ? SipIngressResponseHeaders.Create(request, statusCode, remoteEndPoint)
                        : SipIngressResponseHeaders.EnsureToTag(headers, statusCode),
                    body: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed sending ingress SIP response {Status} for {Method} on {CallId}.",
                statusCode,
                request.Method,
                request.Header("Call-ID"));
        }
    }

}
