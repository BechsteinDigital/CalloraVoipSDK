using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Handles inbound SIP requests for one call session.
/// </summary>
internal sealed class SipCallSessionInboundService
    : IDisposable
{
    private const int DefaultSubscriptionExpiresSeconds = 300;
    private const string SupportedMethodList = "INVITE, ACK, BYE, CANCEL, OPTIONS, INFO, REFER, NOTIFY, UPDATE, PRACK, SUBSCRIBE";

    private readonly ISipCallSessionContext _context;
    private readonly SipCallSessionHeaderService _headers;
    private readonly SipSubscriptionLifecycleManager _subscriptions;

    /// <summary>
    /// Creates a new inbound request handler for one call session context.
    /// </summary>
    public SipCallSessionInboundService(
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
    /// Handles one inbound SIP request associated with this dialog.
    /// </summary>
    public async Task HandleInboundRequestAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct)
    {
        if (_context.IsDisposed) return;
        if (!string.Equals(request.Header("Call-ID"), _context.CallId, StringComparison.Ordinal))
            return;

        // RFC 3261 §12.2.2 (CF-013): a mid-dialog request (one carrying a To-tag) must match this dialog's full
        // identity — Call-ID (matched above) plus our local tag (its To-tag) and the remote tag (its From-tag).
        // A tag mismatch is a request for a different dialog that merely shares the Call-ID (a forked branch, or
        // a stale/foreign request): reject it with 481 rather than mutate this dialog. ACK takes no response and
        // CANCEL is matched by transaction (not dialog identity), so both are exempt from the tag gate.
        if (!string.Equals(request.Method, "CANCEL", StringComparison.Ordinal)
            && !SipDialogIdentity.Matches(
                request.Header("To"), request.Header("From"), _context.LocalTag, _context.RemoteTag))
        {
            if (!string.Equals(request.Method, "ACK", StringComparison.Ordinal))
            {
                var responseTag = _context.LocalTag ?? SipProtocol.NewTag();
                var mismatchHeaders = _headers.CreateResponseHeadersFromRequest(
                    request, responseTag, includeContentType: false);
                await _context.ServerTransactions.SendResponseAsync(
                        request,
                        remoteEndPoint,
                        _context.SignalingTransport,
                        statusCode: 481,
                        reasonPhrase: "Call/Transaction Does Not Exist",
                        mismatchHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
            }
            return;
        }

        _context.RemoteEndPoint = remoteEndPoint;
        _context.TryApplyRemoteAssertedIdentity(
            request.Header("P-Asserted-Identity"),
            remoteEndPoint);
        _context.ApplyInboundDialogRequest(request);
        if (!_context.TryValidateInboundCSeq(
                request,
                out var cseqRejectionStatusCode,
                out var cseqRejectionReasonPhrase,
                out var cseqRetryAfterSeconds))
        {
            var localTag = _context.LocalTag ?? SipProtocol.NewTag();
            _context.LocalTag = localTag;
            var rejectionHeaders = _headers.CreateResponseHeadersFromRequest(
                request,
                localTag,
                includeContentType: false);
            if (cseqRetryAfterSeconds is { } retryAfterSeconds)
                rejectionHeaders["Retry-After"] = retryAfterSeconds.ToString();
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    cseqRejectionStatusCode,
                    cseqRejectionReasonPhrase,
                    rejectionHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Method, "ACK", StringComparison.Ordinal)
            && !string.Equals(request.Method, "CANCEL", StringComparison.Ordinal)
            && !SipRequireOptionPolicy.TryValidateRequestRequireHeader(
                request,
                out var unsupportedHeaderValue))
        {
            var localTag = _context.LocalTag ?? SipProtocol.NewTag();
            _context.LocalTag = localTag;
            var unsupportedHeaders = _headers.CreateResponseHeadersFromRequest(
                request,
                localTag,
                includeContentType: false);
            unsupportedHeaders["Unsupported"] = unsupportedHeaderValue;
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 420,
                    reasonPhrase: "Bad Extension",
                    unsupportedHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "INFO", StringComparison.Ordinal))
        {
            await HandleInfoAsync(remoteEndPoint, request, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "REFER", StringComparison.Ordinal))
        {
            await HandleReferAsync(remoteEndPoint, request, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "NOTIFY", StringComparison.Ordinal))
        {
            await HandleNotifyAsync(remoteEndPoint, request, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "SUBSCRIBE", StringComparison.Ordinal))
        {
            await HandleSubscribeAsync(remoteEndPoint, request, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "OPTIONS", StringComparison.Ordinal))
        {
            await HandleOptionsAsync(remoteEndPoint, request, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "UPDATE", StringComparison.Ordinal))
        {
            await HandleUpdateAsync(remoteEndPoint, request, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "PRACK", StringComparison.Ordinal))
        {
            await HandlePrackAsync(remoteEndPoint, request, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "BYE", StringComparison.Ordinal))
        {
            var terminationReason = TryParseReasonHeader(request.Header("Reason"));
            var localTag = _context.LocalTag ?? SipProtocol.NewTag();
            _context.LocalTag = localTag;
            var headers = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 200,
                    reasonPhrase: "OK",
                    headers,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            _context.TransitionTo(SipDialogState.Terminated, terminationReason);
            return;
        }

        if (string.Equals(request.Method, "INVITE", StringComparison.Ordinal))
        {
            var localTag = _context.LocalTag ?? SipProtocol.NewTag();
            _context.LocalTag = localTag;
            _context.RemoteTag ??= SipProtocol.ExtractTag(request.Header("From"));

            if (_context.HasPendingLocalInviteTransaction)
            {
                var pendingHeaders = _headers.CreateResponseHeadersFromRequest(
                    request,
                    localTag,
                    includeContentType: false);
                await _context.ServerTransactions.SendResponseAsync(
                        request,
                        remoteEndPoint,
                        _context.SignalingTransport,
                        statusCode: 491,
                        reasonPhrase: "Request Pending",
                        pendingHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
                return;
            }

            if (!SipRequireOptionPolicy.TryValidateInviteRequireHeader(
                    request.Headers.TryGetValue("Require", out var inviteRequireHeader)
                        ? inviteRequireHeader
                        : request.Header("Require"),
                    out var inviteUnsupportedHeaderValue))
            {
                var unsupportedHeaders = _headers.CreateResponseHeadersFromRequest(
                    request,
                    localTag,
                    includeContentType: false);
                unsupportedHeaders["Unsupported"] = inviteUnsupportedHeaderValue;
                await _context.ServerTransactions.SendResponseAsync(
                        request,
                        remoteEndPoint,
                        _context.SignalingTransport,
                        statusCode: 420,
                        reasonPhrase: "Bad Extension",
                        unsupportedHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
                return;
            }

            if (!SipContentPolicy.TryValidateSdpRequest(
                    request,
                    out var contentRejectionStatusCode,
                    out var contentRejectionReasonPhrase,
                    out var contentRejectionHeaders))
            {
                var rejectionHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
                if (contentRejectionHeaders is not null)
                {
                    foreach (var pair in contentRejectionHeaders)
                        rejectionHeaders[pair.Key] = pair.Value;
                }

                await _context.ServerTransactions.SendResponseAsync(
                        request,
                        remoteEndPoint,
                        _context.SignalingTransport,
                        contentRejectionStatusCode,
                        contentRejectionReasonPhrase,
                        rejectionHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
                return;
            }

            if (!SipSessionTimerPolicy.TryValidateInboundRequest(
                    request,
                    out var timerRejectionCode,
                    out var timerRejectionReasonPhrase,
                    out var normalizedSessionExpires))
            {
                var timerRejectHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
                if (timerRejectionCode == 422)
                    SipSessionTimerPolicy.ApplyTooSmallResponseHeaders(timerRejectHeaders);
                await _context.ServerTransactions.SendResponseAsync(
                        request,
                        remoteEndPoint,
                        _context.SignalingTransport,
                        statusCode: timerRejectionCode,
                        reasonPhrase: timerRejectionReasonPhrase,
                        timerRejectHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
                return;
            }

            var responseHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: true);
            SipSessionTimerPolicy.ApplyResponseHeaders(responseHeaders, normalizedSessionExpires);
            var localEndPoint = _context.Transport.GetLocalEndPoint(_context.SignalingTransport);
            var responseBody =
                _context.SdpProvider.TryNegotiateAnswer(request.Body, localEndPoint, false)
                ?? _context.SdpProvider.BuildOffer(localEndPoint, false);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 200,
                    reasonPhrase: "OK",
                    responseHeaders,
                    responseBody,
                    ct)
                .ConfigureAwait(false);

            _context.ApplySessionTimerNegotiation(
                responseHeaders.TryGetValue("Session-Expires", out var inviteSessionExpires)
                    ? inviteSessionExpires
                    : null,
                localIsRequester: false);
            if (!string.IsNullOrWhiteSpace(request.Body))
            {
                // Renegotiating re-INVITE (RFC 3264 §8): record the peer's new offer and the
                // answer we sent so the media adapter can rekey. Set before TransitionTo raises
                // the Established event the adapter reacts to.
                _context.SetRemoteSdp(request.Body);
                _context.SetLocalSdp(responseBody);
            }
            var isRemoteHold = _context.SdpProvider.IsRemoteHold(request.Body);
            _context.TransitionTo(isRemoteHold ? SipDialogState.OnHold : SipDialogState.Established);
            _context.NotifyRemoteHoldChanged(isRemoteHold);
            return;
        }

        if (string.Equals(request.Method, "CANCEL", StringComparison.Ordinal)
            && _context.IsInbound
            && _context.State == SipDialogState.Ringing)
        {
            var cancellationReason = TryParseReasonHeader(request.Header("Reason"));
            var localTag = _context.LocalTag ?? SipProtocol.NewTag();
            _context.LocalTag = localTag;
            var cancelHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 200,
                    reasonPhrase: "OK",
                    cancelHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);

            if (_context.InitialInvite is not null)
            {
                var inviteHeaders = _headers.CreateResponseHeadersFromRequest(_context.InitialInvite, localTag, includeContentType: false);
                if (cancellationReason is not null)
                    inviteHeaders["Reason"] = SipReasonHeader.Format(cancellationReason);
                await _context.ServerTransactions.SendResponseAsync(
                        _context.InitialInvite,
                        remoteEndPoint,
                        _context.SignalingTransport,
                        statusCode: 487,
                        reasonPhrase: "Request Terminated",
                        inviteHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
            }

            _context.TransitionTo(SipDialogState.Terminated, cancellationReason);
            return;
        }

        if (string.Equals(request.Method, "CANCEL", StringComparison.Ordinal))
        {
            var localTag = _context.LocalTag ?? SipProtocol.NewTag();
            _context.LocalTag = localTag;
            var notFoundHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 481,
                    reasonPhrase: "Call/Transaction Does Not Exist",
                    notFoundHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "ACK", StringComparison.Ordinal))
            return;

        var fallbackTag = _context.LocalTag ?? SipProtocol.NewTag();
        _context.LocalTag = fallbackTag;
        var notImplementedHeaders = _headers.CreateResponseHeadersFromRequest(
            request,
            fallbackTag,
            includeContentType: false);
        notImplementedHeaders["Allow"] = SupportedMethodList;
        await _context.ServerTransactions.SendResponseAsync(
                request,
                remoteEndPoint,
                _context.SignalingTransport,
                statusCode: 501,
                reasonPhrase: "Not Implemented",
                notImplementedHeaders,
                body: null,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Handles inbound SIP INFO, including DTMF relay payload parsing.
    /// </summary>
    private async Task HandleInfoAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct)
    {
        var localTag = _context.LocalTag ?? SipProtocol.NewTag();
        _context.LocalTag = localTag;
        var contentType = request.Header("Content-Type") ?? string.Empty;

        if (!contentType.Contains("application/dtmf-relay", StringComparison.OrdinalIgnoreCase))
        {
            var unsupportedHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            unsupportedHeaders["Accept"] = "application/dtmf-relay";
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 415,
                    reasonPhrase: "Unsupported Media Type",
                    unsupportedHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

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

        if (TryParseDtmfRelay(request.Body, out var toneCode, out var durationMilliseconds))
        {
            _context.NotifyDtmfReceived(toneCode, durationMilliseconds);
        }
        else
        {
            _context.Logger.LogDebug("SIP INFO DTMF payload could not be parsed on {CallId}.", _context.CallId);
        }
    }

    /// <summary>
    /// Handles inbound SIP REFER and triggers transfer request callback.
    /// </summary>
    private async Task HandleReferAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct)
    {
        var localTag = _context.LocalTag ?? SipProtocol.NewTag();
        _context.LocalTag = localTag;
        var referTo = request.Header("Refer-To");
        var referredBy = request.Header("Referred-By")
            ?? SipProtocol.ExtractUriFromNameAddr(request.Header("From"));

        if (string.IsNullOrWhiteSpace(referTo))
        {
            var badRequestHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    statusCode: 400,
                    reasonPhrase: "Bad Request",
                    badRequestHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var acceptTransfer = _context.NotifyTransferRequested(referTo, referredBy);
        var responseHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
        var statusCode = acceptTransfer ? 202 : 603;
        var reasonPhrase = acceptTransfer ? "Accepted" : "Decline";
        await _context.ServerTransactions.SendResponseAsync(
                request,
                remoteEndPoint,
                _context.SignalingTransport,
                statusCode: statusCode,
                reasonPhrase: reasonPhrase,
                responseHeaders,
                body: null,
                ct)
            .ConfigureAwait(false);

        // RFC 4488: if UAC sent Refer-Sub: false (and norefersub was accepted), skip implicit NOTIFY.
        var referSubHeader = request.Header("Refer-Sub");
        var subscriptionSuppressed = !string.IsNullOrWhiteSpace(referSubHeader)
            && referSubHeader.TrimStart().StartsWith("false", StringComparison.OrdinalIgnoreCase);
        if (!subscriptionSuppressed)
            await SendReferNotifyAsync(acceptTransfer, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles inbound SIP NOTIFY: ACKs with 200 OK, parses Subscription-State, and raises event (RFC 6665 §6.1.1).
    /// </summary>
    private async Task HandleNotifyAsync(
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
    private async Task HandleSubscribeAsync(
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
    /// Handles in-dialog SIP OPTIONS by returning 200 OK capability hint.
    /// </summary>
    private async Task HandleOptionsAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct)
    {
        var localTag = _context.LocalTag ?? SipProtocol.NewTag();
        _context.LocalTag = localTag;
        var headers = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
        headers["Allow"] = SupportedMethodList;
        headers["Accept"] = "application/sdp, application/dtmf-relay, message/sipfrag";
        await _context.ServerTransactions.SendResponseAsync(
                request,
                remoteEndPoint,
                _context.SignalingTransport,
                statusCode: 200,
                reasonPhrase: "OK",
                headers,
                body: null,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Handles in-dialog UPDATE for offer/answer target refresh.
    /// </summary>
    private async Task HandleUpdateAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct)
    {
        var localTag = _context.LocalTag ?? SipProtocol.NewTag();
        _context.LocalTag = localTag;
        if (!SipContentPolicy.TryValidateSdpRequest(
                request,
                out var contentRejectionStatusCode,
                out var contentRejectionReasonPhrase,
                out var contentRejectionHeaders))
        {
            var rejectionHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            if (contentRejectionHeaders is not null)
            {
                foreach (var pair in contentRejectionHeaders)
                    rejectionHeaders[pair.Key] = pair.Value;
            }

            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    contentRejectionStatusCode,
                    contentRejectionReasonPhrase,
                    rejectionHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (!SipSessionTimerPolicy.TryValidateInboundRequest(
                request,
                out var timerRejectionCode,
                out var timerRejectionReasonPhrase,
                out var normalizedSessionExpires))
        {
            var timerRejectHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            if (timerRejectionCode == 422)
                SipSessionTimerPolicy.ApplyTooSmallResponseHeaders(timerRejectHeaders);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    timerRejectionCode,
                    timerRejectionReasonPhrase,
                    timerRejectHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var includeSdp = !string.IsNullOrWhiteSpace(request.Body);
        var headers = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: includeSdp);
        SipSessionTimerPolicy.ApplyResponseHeaders(headers, normalizedSessionExpires);
        string? body = null;
        if (includeSdp)
        {
            var localEndPoint = _context.Transport.GetLocalEndPoint(_context.SignalingTransport);
            body =
                _context.SdpProvider.TryNegotiateAnswer(request.Body, localEndPoint, false)
                ?? _context.SdpProvider.BuildOffer(localEndPoint, false);
        }

        await _context.ServerTransactions.SendResponseAsync(
                request,
                remoteEndPoint,
                _context.SignalingTransport,
                statusCode: 200,
                reasonPhrase: "OK",
                headers,
                body,
                ct)
            .ConfigureAwait(false);

        _context.ApplySessionTimerNegotiation(
            headers.TryGetValue("Session-Expires", out var updateSessionExpires)
                ? updateSessionExpires
                : null,
            localIsRequester: false);
        if (includeSdp)
        {
            // Renegotiating in-dialog request (RFC 3264 §8): record peer offer + our answer for
            // the media adapter's rekey, before the state transition raises its event.
            _context.SetRemoteSdp(request.Body);
            _context.SetLocalSdp(body);
            var isRemoteHold = _context.SdpProvider.IsRemoteHold(request.Body);
            _context.TransitionTo(isRemoteHold ? SipDialogState.OnHold : SipDialogState.Established);
            _context.NotifyRemoteHoldChanged(isRemoteHold);
        }
    }

    /// <summary>
    /// Handles in-dialog PRACK by returning 200 OK.
    /// </summary>
    private async Task HandlePrackAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct)
    {
        var localTag = _context.LocalTag ?? SipProtocol.NewTag();
        _context.LocalTag = localTag;
        if (!_context.TryAcknowledgeReliableProvisional(
                request.Header("RAck"),
                out var rejectionStatusCode,
                out var rejectionReasonPhrase))
        {
            var rejectionHeaders = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
            await _context.ServerTransactions.SendResponseAsync(
                    request,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    rejectionStatusCode,
                    rejectionReasonPhrase,
                    rejectionHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var headers = _headers.CreateResponseHeadersFromRequest(request, localTag, includeContentType: false);
        await _context.ServerTransactions.SendResponseAsync(
                request,
                remoteEndPoint,
                _context.SignalingTransport,
                statusCode: 200,
                reasonPhrase: "OK",
                headers,
                body: null,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one NOTIFY message for REFER subscription completion.
    /// </summary>
    private async Task SendReferNotifyAsync(bool accepted, CancellationToken ct)
    {
        try
        {
            var cseq = _context.NextLocalCSeq();
            var body = accepted ? "SIP/2.0 200 OK" : "SIP/2.0 603 Decline";
            var headers = _headers.CreateDialogRequestHeaders(
                method: "NOTIFY",
                cseq: cseq,
                branch: SipProtocol.NewBranch(),
                authorizationHeaderName: null,
                authorizationHeader: null,
                includeContentType: false);
            headers["Event"] = "refer";
            headers["Subscription-State"] = "terminated;reason=noresource";
            headers["Content-Type"] = "message/sipfrag;version=2.0";

            // RFC 3261 §12.2.1.1 (CF-014): route the in-dialog NOTIFY via the dialog route set / topmost route.
            var (requestUri, remoteEndPoint) =
                await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(_context, headers, ct).ConfigureAwait(false);

            await _context.Transport.SendRequestAsync(
                    "NOTIFY",
                    requestUri,
                    headers,
                    body,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.Logger.LogDebug(ex, "Failed to send REFER NOTIFY on {CallId}.", _context.CallId);
        }
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

    /// <summary>
    /// Tries to parse INFO DTMF relay body.
    /// </summary>
    private static bool TryParseDtmfRelay(
        string body,
        out byte toneCode,
        out int durationMilliseconds)
    {
        toneCode = 0;
        durationMilliseconds = 160;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        string? signal = null;
        foreach (var line in body.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase))
                signal = value;
            else if (key.Equals("Duration", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var parsedDuration))
                durationMilliseconds = Math.Max(40, parsedDuration);
        }

        if (string.IsNullOrWhiteSpace(signal))
            return false;

        var symbol = signal[0];
        toneCode = symbol switch
        {
            >= '0' and <= '9' => (byte)(symbol - '0'),
            '*' => 10,
            '#' => 11,
            'A' or 'a' => 12,
            'B' or 'b' => 13,
            'C' or 'c' => 14,
            'D' or 'd' => 15,
            _ => byte.MaxValue
        };
        return toneCode != byte.MaxValue;
    }

    /// <summary>
    /// Parses one optional RFC3326 Reason header value from an inbound request.
    /// </summary>
    private static SipDialogTerminationReason? TryParseReasonHeader(string? reasonHeader)
    {
        return SipReasonHeader.TryParseFirst(reasonHeader, out var parsedReason)
            ? parsedReason
            : null;
    }

    /// <summary>
    /// Disposes inbound service resources.
    /// </summary>
    public void Dispose()
    {
        _subscriptions.Dispose();
    }
}
