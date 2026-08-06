using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Handles in-dialog SIP event subscriptions (RFC 6665) for one call session: inbound SUBSCRIBE acceptance and
/// lifecycle, inbound NOTIFY acknowledgement, and the NOTIFYs the UAS emits for accepted or expired
/// subscriptions. Extracted from <see cref="SipCallSessionInboundService"/> as an injected collaborator —
/// mirroring <see cref="SipReferHandler"/> — so each keeps a single responsibility and stays within the
/// per-file size limit.
/// </summary>
internal sealed class SipCallSessionSubscriptionService : IDisposable
{
    private const int DefaultSubscriptionExpiresSeconds = 300;

    private readonly ISipCallSessionContext _context;
    private readonly SipCallSessionHeaderService _headers;
    private readonly SipSubscriptionLifecycleManager _subscriptions;

    /// <summary>
    /// Creates a subscription handler for one call session context.
    /// </summary>
    public SipCallSessionSubscriptionService(
        ISipCallSessionContext context,
        SipCallSessionHeaderService headers)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        _subscriptions = new SipSubscriptionLifecycleManager(
            _context.Logger,
            HandleSubscriptionExpiredAsync);
    }

    /// <summary>
    /// Handles inbound SIP NOTIFY: ACKs with 200 OK, parses Subscription-State, and raises event (RFC 6665 §6.1.1).
    /// </summary>
    public async Task HandleNotifyAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct)
    {
        var localTag = _context.LocalTag ?? SipProtocol.NewTag();
        _context.LocalTag = localTag;
        var okHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
        await _context.ServerTransactions.SendResponseAsync(
                request,
                remoteEndPoint,
                _context.SignalingTransport,
                statusCode: 200,
                reasonPhrase: "OK",
                okHeaders,
                body: null,
                ct)
            .ConfigureAwait(false);

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

        _context.Logger.LogDebug(
            "SIP NOTIFY received on {CallId}: event={EventType} state={SubscriptionState}",
            _context.CallId, eventType, subscriptionStateHeader);

        _context.NotifyNotifyReceived(eventType, subscriptionStateHeader, isTerminated, contentType, body);
    }

    /// <summary>
    /// Handles in-dialog SIP SUBSCRIBE by delegating acceptance decision to application callback.
    /// </summary>
    public async Task HandleSubscribeAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct)
    {
        var localTag = _context.LocalTag ?? SipProtocol.NewTag();
        _context.LocalTag = localTag;
        var eventHeader = request.Header("Event");
        var acceptHeader = request.Header("Accept");
        var expiresHeader = request.Header("Expires");
        var expiresSeconds = int.TryParse(expiresHeader, out var parsedExpires)
            ? Math.Max(0, parsedExpires)
            : DefaultSubscriptionExpiresSeconds;

        if (!SipSubscriptionIdentifier.TryParse(eventHeader, out var subscriptionIdentifier))
        {
            var badEventHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 489,
                    reasonPhrase: "Bad Event",
                    badEventHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var accepted = _context.NotifySubscriptionRequested(
            subscriptionIdentifier.EventPackage,
            expiresSeconds,
            acceptHeader);
        var responseHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
        if (!accepted)
        {
            responseHeaders["Expires"] = expiresSeconds.ToString();
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 603,
                    reasonPhrase: "Decline",
                    responseHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        SipSubscriptionLifecycleUpdate lifecycle;
        try
        {
            lifecycle = expiresSeconds == 0
                ? _subscriptions.Terminate(subscriptionIdentifier, reason: "deactivated")
                : _subscriptions.ActivateOrRefresh(subscriptionIdentifier, expiresSeconds);
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "Failed to apply SIP SUBSCRIBE lifecycle update on {CallId}.",
                _context.CallId);
            var errorHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 500,
                    reasonPhrase: "Server Internal Error",
                    errorHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        responseHeaders["Expires"] = lifecycle.EffectiveExpiresSeconds.ToString();
        await _context.ServerTransactions.SendResponseAsync(
                request,
                remoteEndPoint,
                _context.SignalingTransport,
                statusCode: 200,
                reasonPhrase: "OK",
                responseHeaders,
                body: null,
                ct)
            .ConfigureAwait(false);

        await SendSubscriptionNotifyAsync(
                subscriptionIdentifier,
                lifecycle.SubscriptionStateHeader,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one NOTIFY for an accepted SUBSCRIBE request.
    /// </summary>
    private async Task SendSubscriptionNotifyAsync(
        SipSubscriptionIdentifier identifier,
        string subscriptionStateHeader,
        CancellationToken ct)
    {
        try
        {
            var cseq = _context.NextLocalCSeq();
            var headers = _headers.CreateDialogRequestHeaders(
                method: "NOTIFY",
                cseq: cseq,
                branch: SipProtocol.NewBranch(),
                authorizationHeaderName: null,
                authorizationHeader: null,
                includeContentType: false);
            headers["Event"] = identifier.ToEventHeaderValue();
            headers["Subscription-State"] = subscriptionStateHeader;
            headers["Content-Type"] = "message/sipfrag;version=2.0";

            // RFC 3261 §12.2.1.1 (CF-014): route the in-dialog NOTIFY via the dialog route set / topmost route.
            var (requestUri, remoteEndPoint) =
                await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(_context, headers, ct).ConfigureAwait(false);

            await _context.Transport.SendRequestAsync(
                    "NOTIFY",
                    requestUri,
                    headers,
                    "SIP/2.0 200 OK",
                    remoteEndPoint,
                    _context.SignalingTransport,
                    _context.LineTls,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.Logger.LogDebug(ex, "Failed to send SUBSCRIBE NOTIFY on {CallId}.", _context.CallId);
        }
    }

    /// <summary>
    /// Sends timeout NOTIFY when one active subscription lease expires.
    /// </summary>
    private Task HandleSubscriptionExpiredAsync(
        SipSubscriptionIdentifier identifier,
        string reason,
        CancellationToken ct) =>
        SendSubscriptionNotifyAsync(
            identifier,
            $"terminated;reason={reason}",
            ct);

    /// <inheritdoc />
    public void Dispose()
    {
        _subscriptions.Dispose();
    }
}
