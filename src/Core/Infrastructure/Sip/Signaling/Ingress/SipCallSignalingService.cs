using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Default SIP call signaling core service.
/// Handles outbound INVITE flows and inbound INVITE dispatch to call sessions.
/// </summary>
internal sealed class SipCallSignalingService : ISipCallSignalingService
{
    private const string SupportedMethodList = "INVITE, ACK, BYE, CANCEL, OPTIONS, INFO, REFER, NOTIFY, UPDATE, PRACK, SUBSCRIBE";
    private const string SupportedAcceptList = "application/sdp, application/dtmf-relay, message/sipfrag";

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
    private readonly ConcurrentDictionary<string, SipCallSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SipOutboundSubscriptionEntry> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessionStartTimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionTraceIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _replacementTargets = new(StringComparer.Ordinal);
    private readonly SipMergedInviteTracker _mergedInviteTracker = new();
    private readonly IDisposable _requestSubscription;
    private readonly IDisposable _responseSubscription;
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
        ISipUasUserIdentityPolicy? userIdentityPolicy = null)
    {
        var resolvedDigestAuthenticator = digestAuthenticator
            ?? throw new ArgumentNullException(nameof(digestAuthenticator));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _digestAuthenticator = resolvedDigestAuthenticator;
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<SipCallSignalingService>();
        _telemetry = telemetry ?? NullSipTelemetrySink.Instance;
        _identityTrustPolicy = identityTrustPolicy ?? DenyAllSipIdentityTrustPolicy.Instance;
        _userIdentityPolicy = userIdentityPolicy ?? AcceptAllSipUasUserIdentityPolicy.Instance;
        _serverTransactions = new SipServerTransactionEngine(_transport, _logger);
        _subscribeExecutor = new SipClientTransactionExecutor(_transport, _logger);
        _subscriptionService = new SipCallSignalingSubscriptions(
            _transport,
            _digestAuthenticator,
            _subscribeExecutor,
            _subscriptions,
            _logger,
            SendIngressResponseAsync);

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
    public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted;

    /// <inheritdoc />
    public async Task<ISipCallSession> InviteAsync(
        SipInviteRequest request,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateInviteRequest(request);

        var normalizedRemoteUri = SipProtocol.ExtractUriFromNameAddr(request.RemoteUri);
        if (!SipProtocol.TryParseSipUri(normalizedRemoteUri, out _, out _, out _))
            throw new ArgumentException($"RemoteUri must be a valid SIP URI, got '{request.RemoteUri}'.", nameof(request));

        var localUri = $"sip:{request.LocalUsername}@{request.LocalDomain}";
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
        var schemeDowngradeRetryUsed = false;
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
                    CustomHeaders = request.CustomHeaders
                };

                var session = SipCallSession.CreateOutbound(
                    configuration,
                    _sessionDependencies);
                if (!_sessions.TryAdd(callId, session))
                    throw new InvalidOperationException($"Session with Call-ID '{callId}' already exists.");
                HookSessionLifecycle(session);
                _sessionStartTimes[callId] = DateTimeOffset.UtcNow;
                _sessionTraceIds[callId] = traceId;

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
                            visitedRequestUris);
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

                    if (response.StatusCode == 416)
                    {
                        if (!schemeDowngradeRetryUsed
                            && SipOutboundInviteRetryPolicy.TryDowngradeSipsToSip(target.RequestUri, out var downgradedUri)
                            && visitedRequestUris.Add(downgradedUri))
                        {
                            schemeDowngradeRetryUsed = true;
                            pendingTargets.Enqueue(new SipOutboundInviteTarget(
                                RequestUri: downgradedUri,
                                LogicalRemoteUri: downgradedUri,
                                RouteSet: [],
                                NextHopUri: downgradedUri));
                            break;
                        }
                    }

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
    /// Returns true when a thrown invalid operation represents a transport-layer transaction failure.
    /// </summary>
    private static bool IsTransportFailure(InvalidOperationException exception) =>
        SipOutboundInviteRetryPolicy.IsTransportFailure(exception);

    /// <summary>
    /// Removes and disposes one failed outbound session attempt.
    /// </summary>
    private void CleanupFailedOutboundSession(string callId, SipCallSession session)
    {
        _sessions.TryRemove(callId, out _);
        _sessionStartTimes.TryRemove(callId, out _);
        _sessionTraceIds.TryRemove(callId, out _);
        session.Dispose();
    }


    /// <summary>
    /// Handles inbound SIP request dispatch.
    /// </summary>
    private void HandleInboundRequest(IPEndPoint remoteEndPoint, SipRequest request)
    {
        if (request is null) return;
        var inboundTransport = SipIngressRequestPolicy.DetectTransportFromVia(request.Header("Via"));
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
        var registration = _serverTransactions.RegisterInboundRequest(remoteEndPoint, inboundTransport, request);
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

        var normalizedRequest = SipIngressRequestPolicy.DecrementMaxForwardsIfPresent(request);

        if (!string.Equals(normalizedRequest.Method, "ACK", StringComparison.Ordinal)
            && !string.Equals(normalizedRequest.Method, "CANCEL", StringComparison.Ordinal)
            && !SipRequireOptionPolicy.TryValidateRequestRequireHeader(
                normalizedRequest,
                out var unsupportedHeaderValue))
        {
            var unsupportedHeaders = CreateIngressResponseHeaders(normalizedRequest, statusCode: 420);
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
            var rejectionHeaders = CreateIngressResponseHeaders(normalizedRequest, contentRejectionStatusCode);
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
            var headers = CreateIngressResponseHeaders(normalizedRequest, statusCode: 200);
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
            var headers = CreateIngressResponseHeaders(normalizedRequest, statusCode: 501);
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

        var localTag = SipProtocol.NewTag();
        var traceId = ResolveTraceId(callId);
        var configuration = new SipCallSessionConfiguration
        {
            CallId = callId,
            LocalUri = toUri,
            RemoteUri = remoteUri,
            LocalDisplayName = null,
            PreferredIdentityUri = null,
            AuthUsername = string.Empty,
            AuthPassword = null,
            UserAgent = "CalloraVoipSdk/1.0",
            Timeout = TimeSpan.FromSeconds(30),
            RemoteEndPoint = remoteEndPoint,
            SignalingTransport = inboundTransport
        };
        var inboundContext = new SipInboundSessionContext
        {
            InitialInvite = normalizedRequest,
            LocalTag = localTag
        };

        var session = SipCallSession.CreateInbound(
            configuration,
            inboundContext,
            _sessionDependencies);

        if (!_sessions.TryAdd(callId, session))
        {
            session.Dispose();
            return;
        }
        if (!string.IsNullOrWhiteSpace(replacesTargetCallId))
            _replacementTargets[callId] = replacesTargetCallId;

        HookSessionLifecycle(session);
        _sessionStartTimes[callId] = DateTimeOffset.UtcNow;
        _sessionTraceIds[callId] = traceId;
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
                && _replacementTargets.TryRemove(session.CallId, out var replacedCallId))
            {
                _ = TerminateReplacedDialogAsync(session.CallId, replacedCallId, traceId);
            }

            if (e.NewState != SipDialogState.Terminated) return;
            _sessions.TryRemove(session.CallId, out var _);
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
    private async Task TerminateReplacedDialogAsync(
        string replacingCallId,
        string replacedCallId,
        string traceId)
    {
        try
        {
            if (!_sessions.TryGetValue(replacedCallId, out var replacedSession))
                return;
            if (string.Equals(replacingCallId, replacedCallId, StringComparison.Ordinal))
                return;
            if (replacedSession.State == SipDialogState.Terminated)
                return;

            var reason = SipReasonHeader.CreateSipStatusReason(200, "Replaced");
            await replacedSession.HangupAsync(reason: reason).ConfigureAwait(false);
            _telemetry.PublishEvent(new SipEventRecord
            {
                EventType = "sip.dialog.replaces.completed",
                CallId = replacingCallId,
                CorrelationId = BuildCorrelationId(replacingCallId, "REPLACES", null),
                TraceId = traceId,
                Attributes = new Dictionary<string, string>
                {
                    ["replaced_call_id"] = replacedCallId
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed terminating replaced SIP dialog {ReplacedCallId} for replacing dialog {ReplacingCallId}.",
                replacedCallId,
                replacingCallId);
        }
    }

    /// <inheritdoc />
    public Task<SipSubscriptionHandle> SubscribeAsync(
        SipSubscribeRequest request,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _subscriptionService.SubscribeAsync(request, ct);
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
        _serverTransactions.Dispose();
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        _sessionStartTimes.Clear();
        _sessionTraceIds.Clear();
        _replacementTargets.Clear();
        foreach (var sub in _subscriptions.Values)
            sub.RefreshCts.Cancel();
        _subscriptions.Clear();
    }

    /// <summary>
    /// Validates outbound INVITE request input.
    /// </summary>
    private static void ValidateInviteRequest(SipInviteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.LocalUsername))
            throw new ArgumentException("LocalUsername is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.LocalDomain))
            throw new ArgumentException("LocalDomain is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RemoteUri))
            throw new ArgumentException("RemoteUri is required.", nameof(request));
        if (!string.IsNullOrWhiteSpace(request.PreferredIdentityUri))
        {
            var preferredIdentityUri = SipProtocol.ExtractUriFromNameAddr(request.PreferredIdentityUri)
                ?? request.PreferredIdentityUri;
            if (!SipProtocol.TryParseSipUri(
                    preferredIdentityUri,
                    out _,
                    out _,
                    out _))
            {
                throw new ArgumentException(
                    $"PreferredIdentityUri must be a valid SIP URI, got '{request.PreferredIdentityUri}'.",
                    nameof(request));
            }
        }
        if (request.RemotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), "RemotePort must be between 1 and 65535.");
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be positive.");
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
    /// Builds lightweight trace correlation key for SIP events.
    /// </summary>
    private static string BuildCorrelationId(string callId, string operation, string? tag) =>
        string.IsNullOrWhiteSpace(tag)
            ? $"{callId}:{operation}"
            : $"{callId}:{operation}:{tag}";

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
                        ? CreateIngressResponseHeaders(request, statusCode, remoteEndPoint)
                        : EnsureIngressResponseToTag(headers, statusCode),
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

    /// <summary>
    /// Creates minimal response headers for ingress-level replies.
    /// </summary>
    private static Dictionary<string, string> CreateIngressResponseHeaders(
        SipRequest request,
        int statusCode,
        IPEndPoint? remoteEndPoint = null)
    {
        // RFC 3581 §4: reflect rport/received into the Via header of responses.
        var viaValue = request.Header("Via") ?? string.Empty;
        if (remoteEndPoint is not null)
            viaValue = SipProtocol.ReflectViaRport(viaValue, remoteEndPoint);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = viaValue,
            ["From"] = request.Header("From") ?? string.Empty,
            ["To"] = request.Header("To") ?? string.Empty,
            ["Call-ID"] = request.Header("Call-ID") ?? string.Empty,
            ["CSeq"] = request.Header("CSeq") ?? string.Empty,
            ["Supported"] = "100rel, timer, replaces",
            ["Server"] = "CalloraVoipSdk/1.0",
            ["Date"] = DateTimeOffset.UtcNow.ToString("r"),
            ["User-Agent"] = "CalloraVoipSdk/1.0"
        };

        // RFC 3261 §8.2.6.2: Record-Route MUST be copied verbatim from request to response.
        var recordRoute = request.Header("Record-Route");
        if (!string.IsNullOrWhiteSpace(recordRoute))
            headers["Record-Route"] = recordRoute;

        return EnsureIngressResponseToTag(headers, statusCode);
    }

    /// <summary>
    /// Ensures To tag presence for non-100 UAS responses.
    /// </summary>
    private static Dictionary<string, string> EnsureIngressResponseToTag(
        IReadOnlyDictionary<string, string> headers,
        int statusCode)
    {
        var mutable = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        if (statusCode <= 100)
            return mutable;

        var currentTo = mutable.TryGetValue("To", out var toHeaderValue)
            ? toHeaderValue
            : string.Empty;
        mutable["To"] = SipCallSessionHeaderService.EnsureTag(currentTo, SipProtocol.NewTag());
        return mutable;
    }

    /// <summary>
    /// Returns true when method semantically requires an existing SIP dialog.
    /// </summary>
    private static bool IsDialogScopedMethod(string method)
    {
        var normalized = method.Trim().ToUpperInvariant();
        return normalized is "BYE" or "INFO" or "UPDATE" or "PRACK" or "REFER" or "NOTIFY" or "SUBSCRIBE";
    }

    /// <summary>
    /// Resolves effective remote port with SIPS default handling.
    /// </summary>
    private static int ResolveDefaultRemotePort(int configuredPort, bool secureTarget)
    {
        if (secureTarget && configuredPort == 5060)
            return 5061;
        return configuredPort;
    }

    /// <summary>
    /// Resolves a deterministic trace identifier for SIP dialog observability.
    /// </summary>
    private static string ResolveTraceId(string fallback) =>
        Activity.Current?.TraceId.ToString() ?? fallback;
}
