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
/// Sends out-of-dialog SIP MESSAGE requests (RFC 3428 pager-mode IM) through the shared client-transaction
/// executor, answering a 401/407 challenge with long-term digest credentials (RFC 3261 §22). Each MESSAGE is
/// an independent transaction — it opens no dialog and keeps no state between calls.
/// </summary>
internal sealed class SipCallSignalingMessages
{
    private readonly ISipTransportRuntime _transport;
    private readonly ISipDigestAuthenticator _digestAuthenticator;
    private readonly SipClientTransactionExecutor _executor;
    private readonly ILogger _logger;

    /// <summary>Creates the MESSAGE sender over the shared transport, digest authenticator and executor.</summary>
    public SipCallSignalingMessages(
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
    /// Sends one out-of-dialog MESSAGE and returns the final response status code (a 2xx on success).
    /// Tries each resolved route in turn; answers a single 401/407 challenge when a password is supplied.
    /// </summary>
    public async Task<int> SendMessageAsync(SipMessageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.LocalUsername))
            throw new ArgumentException("LocalUsername is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.LocalDomain))
            throw new ArgumentException("LocalDomain is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RemoteUri))
            throw new ArgumentException("RemoteUri is required.", nameof(request));

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

        var cseq = 1;
        var nonceCounter = new SipNonceCounter();
        var attempted = false;

        foreach (var routeCandidate in routeCandidates)
        {
            ct.ThrowIfCancellationRequested();
            attempted = true;
            var branch = SipProtocol.NewBranch();
            var headers = BuildMessageHeaders(
                localEndPoint, branch, routeCandidate.Transport, fromHeader, toHeader, callId, cseq, contentType, body);

            SipResponse response;
            try
            {
                var result = await _executor
                    .ExecuteAsync(BuildTransaction(normalizedRemoteUri, headers, body, routeCandidate, request.Timeout), ct)
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
                    challengeHeader, request.LocalUsername, request.AuthPassword!, "MESSAGE",
                    normalizedRemoteUri, nonceCounter.NextFor(challengeHeader), out var authorizationHeader))
            {
                cseq++;
                var retryBranch = SipProtocol.NewBranch();
                var retryHeaders = BuildMessageHeaders(
                    localEndPoint, retryBranch, routeCandidate.Transport, fromHeader, toHeader, callId, cseq, contentType, body);
                retryHeaders[authResultHeaderName] = authorizationHeader;
                try
                {
                    var retryResult = await _executor
                        .ExecuteAsync(BuildTransaction(normalizedRemoteUri, retryHeaders, body, routeCandidate, request.Timeout), ct)
                        .ConfigureAwait(false);
                    response = retryResult.FinalResponse.Response;
                }
                catch (TimeoutException)
                {
                    continue;
                }
            }

            _logger.LogDebug("SIP MESSAGE to {Target} completed with {Status}.", normalizedRemoteUri, response.StatusCode);
            return response.StatusCode;
        }

        throw new TimeoutException(
            attempted
                ? $"SIP MESSAGE to {normalizedRemoteUri} received no response."
                : $"No route could be resolved for SIP MESSAGE to {normalizedRemoteUri}.");
    }

    private static SipClientTransactionRequest BuildTransaction(
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string body,
        SipRouteCandidate route,
        TimeSpan timeout) => new()
    {
        Method = "MESSAGE",
        RequestUri = requestUri,
        Headers = headers,
        Body = body,
        RemoteEndPoint = route.EndPoint,
        Transport = route.Transport,
        Timeout = timeout
    };

    private static Dictionary<string, string> BuildMessageHeaders(
        IPEndPoint localEndPoint,
        string branch,
        SipTransportProtocol transport,
        string fromHeader,
        string toHeader,
        string callId,
        int cseq,
        string contentType,
        string body) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = SipSignalingFormat.BuildVia(localEndPoint, branch, transport),
            ["Max-Forwards"] = "70",
            ["From"] = fromHeader,
            ["To"] = toHeader,
            ["Call-ID"] = callId,
            ["CSeq"] = $"{cseq} MESSAGE",
            ["Content-Type"] = contentType,
            ["User-Agent"] = "CalloraVoipSdk/1.0",
            ["Content-Length"] = Encoding.UTF8.GetByteCount(body).ToString(CultureInfo.InvariantCulture)
        };
}
