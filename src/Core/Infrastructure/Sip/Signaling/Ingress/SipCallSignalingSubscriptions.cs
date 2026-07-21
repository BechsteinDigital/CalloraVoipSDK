using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

internal sealed class SipCallSignalingSubscriptions
{
    private readonly ISipTransportRuntime _transport;
    private readonly ISipDigestAuthenticator _digestAuthenticator;
    private readonly SipClientTransactionExecutor _subscribeExecutor;
    private readonly ConcurrentDictionary<string, SipOutboundSubscriptionEntry> _subscriptions;
    private readonly ILogger _logger;
    private readonly Func<SipRequest, IPEndPoint, SipTransportProtocol, int, string, IReadOnlyDictionary<string, string>?, Task> _sendIngressResponseAsync;

    public SipCallSignalingSubscriptions(
        ISipTransportRuntime transport,
        ISipDigestAuthenticator digestAuthenticator,
        SipClientTransactionExecutor subscribeExecutor,
        ConcurrentDictionary<string, SipOutboundSubscriptionEntry> subscriptions,
        ILogger logger,
        Func<SipRequest, IPEndPoint, SipTransportProtocol, int, string, IReadOnlyDictionary<string, string>?, Task> sendIngressResponseAsync)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _digestAuthenticator = digestAuthenticator ?? throw new ArgumentNullException(nameof(digestAuthenticator));
        _subscribeExecutor = subscribeExecutor ?? throw new ArgumentNullException(nameof(subscribeExecutor));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sendIngressResponseAsync = sendIngressResponseAsync ?? throw new ArgumentNullException(nameof(sendIngressResponseAsync));
    }

    public async Task<SipSubscriptionHandle> SubscribeAsync(
        SipSubscribeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.LocalUsername))
            throw new ArgumentException("LocalUsername is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.LocalDomain))
            throw new ArgumentException("LocalDomain is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RemoteUri))
            throw new ArgumentException("RemoteUri is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EventType))
            throw new ArgumentException("EventType is required.", nameof(request));

        var normalizedRemoteUri = SipProtocol.ExtractUriFromNameAddr(request.RemoteUri) ?? request.RemoteUri;
        if (!SipProtocol.TryParseSipUri(normalizedRemoteUri, out _, out var targetHost, out var targetPortFromUri))
            throw new ArgumentException($"RemoteUri must be a valid SIP URI, got '{request.RemoteUri}'.", nameof(request));

        var localUri = $"sip:{request.LocalUsername}@{request.LocalDomain}";
        var callId = SipProtocol.NewCallId();
        var localTag = SipProtocol.NewTag();

        var secureTarget = SipProtocol.IsSipsUri(normalizedRemoteUri);
        var targetPort = targetPortFromUri ?? (secureTarget ? 5061 : 5060);
        var routeCandidates = await _transport.ResolveRemoteRouteCandidatesAsync(
                targetHost,
                targetPort,
                request.Transport,
                ct)
            .ConfigureAwait(false);

        var localEndPoint = _transport.GetLocalEndPoint(request.Transport);
        var fromHeader = SipProtocol.FormatNameAddr(displayName: null, localUri, localTag);
        var toHeader = SipProtocol.FormatNameAddr(displayName: null, normalizedRemoteUri);

        var expiresSeconds = Math.Max(0, request.ExpiresSeconds);
        var cseq = 1;
        string? authorizationHeader = null;
        string? authorizationHeaderName = null;
        var nonceCounter = new SipNonceCounter();

        SipResponse? finalResponse = null;
        IPEndPoint? chosenEndPoint = null;
        SipTransportProtocol chosenTransport = request.Transport;

        foreach (var routeCandidate in routeCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var branch = SipProtocol.NewBranch();
            var contactUri = SipSignalingFormat.BuildContactUri(
                request.LocalUsername, localEndPoint, routeCandidate.Transport);
            var headers = BuildSubscribeHeaders(
                localEndPoint, branch, routeCandidate.Transport,
                fromHeader, toHeader, callId, cseq,
                contactUri, request.EventType, expiresSeconds, request.AcceptHeader);

            if (!string.IsNullOrWhiteSpace(authorizationHeader) && !string.IsNullOrWhiteSpace(authorizationHeaderName))
                headers[authorizationHeaderName] = authorizationHeader;

            SipResponse response;
            try
            {
                var result = await _subscribeExecutor.ExecuteAsync(
                        new SipClientTransactionRequest
                        {
                            Method = "SUBSCRIBE",
                            RequestUri = normalizedRemoteUri,
                            Headers = headers,
                            Body = null,
                            RemoteEndPoint = routeCandidate.EndPoint,
                            Transport = routeCandidate.Transport,
                            Timeout = request.Timeout
                        },
                        ct)
                    .ConfigureAwait(false);
                response = result.FinalResponse.Response;
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (response.StatusCode == 401 || response.StatusCode == 407)
            {
                // RFC 3261 §22 (CF-043): pick the strongest offered challenge via the shared selector instead of
                // blindly taking the first WWW-/Proxy-Authenticate header (the selector also resolves the correct
                // Authorization vs Proxy-Authorization result header for a 401 vs 407).
                if (!string.IsNullOrWhiteSpace(request.AuthPassword)
                    && SipDigestChallengeSelector.TrySelect(response, out var challengeHeader, out var authResultHeaderName)
                    && _digestAuthenticator.TryCreateAuthorizationHeader(
                        challengeHeader,
                        request.LocalUsername,
                        request.AuthPassword,
                        "SUBSCRIBE",
                        normalizedRemoteUri,
                        nonceCounter.NextFor(challengeHeader),
                        out authorizationHeader))
                {
                    authorizationHeaderName = authResultHeaderName;
                    cseq++;
                    var retryBranch = SipProtocol.NewBranch();
                    var retryHeaders = BuildSubscribeHeaders(
                        localEndPoint, retryBranch, routeCandidate.Transport,
                        fromHeader, toHeader, callId, cseq,
                        contactUri, request.EventType, expiresSeconds, request.AcceptHeader);
                    retryHeaders[authorizationHeaderName] = authorizationHeader;
                    try
                    {
                        var retryResult = await _subscribeExecutor.ExecuteAsync(
                                new SipClientTransactionRequest
                                {
                                    Method = "SUBSCRIBE",
                                    RequestUri = normalizedRemoteUri,
                                    Headers = retryHeaders,
                                    Body = null,
                                    RemoteEndPoint = routeCandidate.EndPoint,
                                    Transport = routeCandidate.Transport,
                                    Timeout = request.Timeout
                                },
                                ct)
                            .ConfigureAwait(false);
                        response = retryResult.FinalResponse.Response;
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                }
            }

            if (SipProtocol.IsSuccess(response.StatusCode) || response.StatusCode == 202)
            {
                finalResponse = response;
                chosenEndPoint = routeCandidate.EndPoint;
                chosenTransport = routeCandidate.Transport;
                break;
            }
        }

        if (finalResponse is null || chosenEndPoint is null)
            throw new InvalidOperationException($"SIP SUBSCRIBE to '{request.RemoteUri}' failed: no successful response.");

        var negotiatedExpires = int.TryParse(finalResponse.Header("Expires"), out var parsedExpires)
            ? Math.Max(60, parsedExpires)
            : expiresSeconds;
        var remoteTag = SipProtocol.ExtractTag(finalResponse.Header("To"));

        SipSubscriptionHandle? handle = null;
        var entry = new SipOutboundSubscriptionEntry
        {
            CallId = callId,
            EventType = request.EventType,
            RequestUri = normalizedRemoteUri,
            LocalUri = localUri,
            RemoteUri = normalizedRemoteUri,
            LocalTag = localTag,
            RemoteTag = remoteTag,
            RemoteEndPoint = chosenEndPoint,
            Transport = chosenTransport,
            Timeout = request.Timeout,
            AuthPassword = request.AuthPassword,
            AuthUsername = request.LocalUsername,
            AcceptHeader = request.AcceptHeader,
            LocalCSeq = cseq,
            ExpiresSeconds = negotiatedExpires
        };

        handle = new SipSubscriptionHandle(async unsubscribeCt =>
        {
            entry.RefreshCts.Cancel();
            _subscriptions.TryRemove(callId, out _);
            try
            {
                await SendSubscribeRefreshAsync(entry, expiresSeconds: 0, unsubscribeCt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP unsubscribe SUBSCRIBE failed for {CallId}.", callId);
            }
        });
        entry.Handle = handle;

        _subscriptions[callId] = entry;
        _ = RunSubscriptionRefreshAsync(entry, entry.RefreshCts.Token);

        return handle;
    }

    public void HandleInboundSubscriptionNotify(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        SipTransportProtocol transport,
        SipOutboundSubscriptionEntry entry)
    {
        _ = _sendIngressResponseAsync(request, remoteEndPoint, transport, 200, "OK", null);

        var eventHeader = request.Header("Event") ?? string.Empty;
        var eventType = eventHeader.Contains(';')
            ? eventHeader[..eventHeader.IndexOf(';')].Trim()
            : eventHeader.Trim();
        var subscriptionStateHeader = request.Header("Subscription-State") ?? string.Empty;
        var isTerminated = subscriptionStateHeader.StartsWith("terminated", StringComparison.OrdinalIgnoreCase);
        var contentType = string.IsNullOrWhiteSpace(request.Header("Content-Type"))
            ? null
            : request.Header("Content-Type");
        var body = string.IsNullOrWhiteSpace(request.Body) ? null : request.Body;

        _logger.LogDebug(
            "SIP out-of-dialog NOTIFY received for subscription {CallId}: event={EventType} state={State}",
            entry.CallId,
            eventType,
            subscriptionStateHeader);

        entry.Handle?.RaiseNotifyReceived(
            new SipNotifyReceivedEventArgs(eventType, subscriptionStateHeader, isTerminated, contentType, body));

        if (isTerminated)
        {
            entry.RefreshCts.Cancel();
            _subscriptions.TryRemove(entry.CallId, out _);
        }
    }

    private async Task SendSubscribeRefreshAsync(
        SipOutboundSubscriptionEntry entry,
        int expiresSeconds,
        CancellationToken ct)
    {
        var localEndPoint = _transport.GetLocalEndPoint(entry.Transport);
        var contactUri = SipSignalingFormat.BuildContactUri(
            entry.AuthUsername,
            localEndPoint,
            entry.Transport);
        var cseq = entry.LocalCSeq + 1;
        entry.LocalCSeq = cseq;
        var fromHeader = SipProtocol.FormatNameAddr(displayName: null, entry.LocalUri, entry.LocalTag);
        var toHeader = string.IsNullOrWhiteSpace(entry.RemoteTag)
            ? SipProtocol.FormatNameAddr(displayName: null, entry.RemoteUri)
            : SipProtocol.FormatNameAddr(displayName: null, entry.RemoteUri, entry.RemoteTag);
        var branch = SipProtocol.NewBranch();
        var headers = BuildSubscribeHeaders(
            localEndPoint,
            branch,
            entry.Transport,
            fromHeader,
            toHeader,
            entry.CallId,
            cseq,
            contactUri,
            entry.EventType,
            expiresSeconds,
            entry.AcceptHeader);

        var result = await _subscribeExecutor.ExecuteAsync(
                new SipClientTransactionRequest
                {
                    Method = "SUBSCRIBE",
                    RequestUri = entry.RequestUri,
                    Headers = headers,
                    Body = null,
                    RemoteEndPoint = entry.RemoteEndPoint,
                    Transport = entry.Transport,
                    Timeout = entry.Timeout
                },
                ct)
            .ConfigureAwait(false);

        if (int.TryParse(result.FinalResponse.Response.Header("Expires"), out var newExpires))
            entry.ExpiresSeconds = Math.Max(60, newExpires);
    }

    private async Task RunSubscriptionRefreshAsync(SipOutboundSubscriptionEntry entry, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(entry.ExpiresSeconds * 0.9);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested)
                break;

            try
            {
                await SendSubscribeRefreshAsync(entry, entry.ExpiresSeconds, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "SIP subscription refresh failed for {CallId}.", entry.CallId);
            }
        }
    }

    private static Dictionary<string, string> BuildSubscribeHeaders(
        IPEndPoint localEndPoint,
        string branch,
        SipTransportProtocol transport,
        string fromHeader,
        string toHeader,
        string callId,
        int cseq,
        string contactUri,
        string eventType,
        int expiresSeconds,
        string? acceptHeader)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = SipSignalingFormat.BuildVia(localEndPoint, branch, transport),
            ["Max-Forwards"] = "70",
            ["From"] = fromHeader,
            ["To"] = toHeader,
            ["Call-ID"] = callId,
            ["CSeq"] = $"{cseq} SUBSCRIBE",
            ["Contact"] = $"<{contactUri}>",
            ["Event"] = eventType,
            ["Expires"] = expiresSeconds.ToString(),
            ["Supported"] = "100rel, timer, replaces",
            ["User-Agent"] = "CalloraVoipSdk/1.0",
            ["Content-Length"] = "0"
        };
        if (!string.IsNullOrWhiteSpace(acceptHeader))
            headers["Accept"] = acceptHeader;

        return headers;
    }
}
