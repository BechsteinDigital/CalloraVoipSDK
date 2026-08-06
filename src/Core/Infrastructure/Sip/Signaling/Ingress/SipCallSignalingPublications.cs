using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Routing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Sends out-of-dialog SIP PUBLISH requests (RFC 3903 event state publication) through the shared
/// client-transaction executor, answering a 401/407 challenge with long-term digest credentials
/// (RFC 3261 §22). Each PUBLISH is an independent transaction; the entity-tag/lifetime a 2xx returns is
/// surfaced to the caller for later refresh, modify or remove (SIP-If-Match).
/// </summary>
internal sealed class SipCallSignalingPublications
{
    private readonly ISipTransportRuntime _transport;
    private readonly ISipDigestAuthenticator _digestAuthenticator;
    private readonly SipClientTransactionExecutor _executor;
    private readonly ILogger _logger;

    /// <summary>Creates the PUBLISH sender over the shared transport, digest authenticator and executor.</summary>
    public SipCallSignalingPublications(
        ISipTransportRuntime transport,
        ISipDigestAuthenticator digestAuthenticator,
        SipClientTransactionExecutor executor,
        ILogger logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _digestAuthenticator = digestAuthenticator ?? throw new ArgumentNullException(nameof(digestAuthenticator));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends one out-of-dialog PUBLISH and returns the final status code plus the SIP-ETag and granted
    /// Expires from a 2xx. Tries each resolved route in turn; answers a single 401/407 challenge when a
    /// password is supplied.
    /// </summary>
    public async Task<SipPublishResult> PublishAsync(SipPublishRequest request, CancellationToken ct = default)
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
        var routeCandidates = await _transport
            .ResolveRemoteRouteCandidatesAsync(targetHost, targetPort, request.Transport, ct)
            .ConfigureAwait(false);

        var localEndPoint = _transport.GetLocalEndPoint(request.Transport);
        var fromHeader = SipProtocol.FormatNameAddr(displayName: null, localUri, localTag);
        var toHeader = SipProtocol.FormatNameAddr(displayName: null, normalizedRemoteUri);
        var body = request.Body ?? string.Empty;
        var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "text/plain" : request.ContentType;
        var expires = Math.Max(0, request.ExpiresSeconds);

        var cseq = 1;
        var nonceCounter = new SipNonceCounter();
        var attempted = false;

        foreach (var routeCandidate in routeCandidates)
        {
            ct.ThrowIfCancellationRequested();
            attempted = true;
            var branch = SipProtocol.NewBranch();
            var headers = BuildPublishHeaders(
                localEndPoint, branch, routeCandidate.Transport, fromHeader, toHeader, callId, cseq,
                request.EventType, expires, contentType, body, request.IfMatch);

            SipResponse response;
            try
            {
                var result = await _executor
                    .ExecuteAsync(BuildTransaction(normalizedRemoteUri, headers, body, routeCandidate, request.Timeout, request.LineTls), ct)
                    .ConfigureAwait(false);
                response = result.FinalResponse.Response;
            }
            catch (TimeoutException)
            {
                continue; // try the next resolved route
            }

            if ((response.StatusCode == 401 || response.StatusCode == 407)
                && !string.IsNullOrWhiteSpace(request.AuthPassword)
                && SipDigestChallengeSelector.TrySelect(response, out var challengeHeader, out var authResultHeaderName)
                && _digestAuthenticator.TryCreateAuthorizationHeader(
                    challengeHeader, request.LocalUsername, request.AuthPassword!, "PUBLISH",
                    normalizedRemoteUri, nonceCounter.NextFor(challengeHeader), out var authorizationHeader))
            {
                cseq++;
                var retryBranch = SipProtocol.NewBranch();
                var retryHeaders = BuildPublishHeaders(
                    localEndPoint, retryBranch, routeCandidate.Transport, fromHeader, toHeader, callId, cseq,
                    request.EventType, expires, contentType, body, request.IfMatch);
                retryHeaders[authResultHeaderName] = authorizationHeader;
                try
                {
                    var retryResult = await _executor
                        .ExecuteAsync(BuildTransaction(normalizedRemoteUri, retryHeaders, body, routeCandidate, request.Timeout, request.LineTls), ct)
                        .ConfigureAwait(false);
                    response = retryResult.FinalResponse.Response;
                }
                catch (TimeoutException)
                {
                    continue;
                }
            }

            _logger.LogDebug("SIP PUBLISH to {Target} completed with {Status}.", normalizedRemoteUri, response.StatusCode);
            return BuildResult(response);
        }

        throw new TimeoutException(
            attempted
                ? $"SIP PUBLISH to {normalizedRemoteUri} received no response."
                : $"No route could be resolved for SIP PUBLISH to {normalizedRemoteUri}.");
    }

    // On a 2xx the compositor returns SIP-ETag (RFC 3903 §4) and the granted Expires; both are absent/0
    // on a failure response, which the result then carries alongside the status code.
    private static SipPublishResult BuildResult(SipResponse response)
    {
        var etag = response.Header("SIP-ETag");
        var granted = int.TryParse(response.Header("Expires"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : 0;
        return new SipPublishResult(response.StatusCode, string.IsNullOrWhiteSpace(etag) ? null : etag, granted);
    }

    private static SipClientTransactionRequest BuildTransaction(
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string body,
        SipRouteCandidate route,
        TimeSpan timeout,
        CalloraVoipSdk.Core.Application.Ports.Security.TlsConfiguration? lineTls) => new()
    {
        Method = "PUBLISH",
        RequestUri = requestUri,
        Headers = headers,
        Body = body,
        RemoteEndPoint = route.EndPoint,
        Transport = route.Transport,
        Timeout = timeout,
        LineTls = lineTls
    };

    private static Dictionary<string, string> BuildPublishHeaders(
        IPEndPoint localEndPoint,
        string branch,
        SipTransportProtocol transport,
        string fromHeader,
        string toHeader,
        string callId,
        int cseq,
        string eventType,
        int expires,
        string contentType,
        string body,
        string? ifMatch)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = SipSignalingFormat.BuildVia(localEndPoint, branch, transport),
            ["Max-Forwards"] = "70",
            ["From"] = fromHeader,
            ["To"] = toHeader,
            ["Call-ID"] = callId,
            ["CSeq"] = $"{cseq} PUBLISH",
            ["Event"] = eventType,
            ["Expires"] = expires.ToString(CultureInfo.InvariantCulture),
            ["User-Agent"] = "CalloraVoipSdk/1.0",
            ["Content-Length"] = Encoding.UTF8.GetByteCount(body).ToString(CultureInfo.InvariantCulture)
        };

        // RFC 3903 §4: an update (refresh/modify/remove) targets a prior publication by its entity-tag.
        if (!string.IsNullOrWhiteSpace(ifMatch))
            headers["SIP-If-Match"] = ifMatch;

        // RFC 3903 §6 / RFC 3261 §20.15: a bodyless PUBLISH (refresh/remove) carries no Content-Type.
        if (body.Length > 0)
            headers["Content-Type"] = contentType;

        return headers;
    }
}
