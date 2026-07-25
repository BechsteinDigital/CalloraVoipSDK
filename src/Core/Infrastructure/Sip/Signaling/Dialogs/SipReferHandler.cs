using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Handles inbound SIP REFER for one call session: sends the 202/603, raises the transfer-request callback, and
/// drives the implicit subscription's <c>message/sipfrag</c> NOTIFYs (RFC 3515 §2.4 / RFC 6665). Extracted from
/// <see cref="SipCallSessionInboundService"/> so the REFER concern owns its own NOTIFY sender, which the consumer
/// progress handle (<see cref="SipReferSubscription"/>) reuses.
/// </summary>
internal sealed class SipReferHandler
{
    private readonly ISipCallSessionContext _context;
    private readonly SipCallSessionHeaderService _headers;

    /// <summary>Creates the REFER handler bound to one call-session context and its header service.</summary>
    public SipReferHandler(ISipCallSessionContext context, SipCallSessionHeaderService headers)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
    }

    /// <summary>
    /// Handles inbound SIP REFER and triggers the transfer-request callback.
    /// </summary>
    public async Task HandleReferAsync(
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

        // RFC 4488: if UAC sent Refer-Sub: false (and norefersub was accepted), no implicit subscription is
        // created — the handle stays inert so consumer progress reports produce no NOTIFY.
        var referSubHeader = request.Header("Refer-Sub");
        var subscriptionSuppressed = !string.IsNullOrWhiteSpace(referSubHeader)
            && referSubHeader.TrimStart().StartsWith("false", StringComparison.OrdinalIgnoreCase);

        // Handle created before the callback so the consumer may report synchronously inside the transfer
        // handler; such reports are buffered and flushed by StartAsync after the 202.
        var subscription = new SipReferSubscription(
            SendReferNotifyMessageAsync, sessionShutdown: _context.SessionShutdownToken);
        var acceptTransfer = _context.NotifyTransferRequested(referTo, referredBy, subscription);

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

        if (subscriptionSuppressed)
        {
            subscription.Cancel();
            return;
        }

        if (acceptTransfer)
        {
            // RFC 3515 §2.4.4 / RFC 6665: emit the immediate active/100 Trying, then relay whatever progress and
            // outcome the consumer reports through the handle (none → the transferor's subscription lapses at expiry).
            await subscription.StartAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // A declined REFER (603) terminates the subscription immediately with a single NOTIFY.
            subscription.Cancel();
            await SendReferNotifyMessageAsync("terminated;reason=noresource", "SIP/2.0 603 Decline", ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends one NOTIFY on the REFER implicit subscription with the given Subscription-State and message/sipfrag
    /// body (RFC 3515 §2.4.5 / RFC 6665). Failures are logged and swallowed so one lost NOTIFY does not abort the
    /// REFER handling. Also used as the send delegate of <see cref="SipReferSubscription"/>.
    /// </summary>
    private async Task SendReferNotifyMessageAsync(string subscriptionState, string sipfrag, CancellationToken ct)
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
            headers["Event"] = "refer";
            headers["Subscription-State"] = subscriptionState;
            headers["Content-Type"] = "message/sipfrag;version=2.0";

            // RFC 3261 §12.2.1.1 (CF-014): route the in-dialog NOTIFY via the dialog route set / topmost route.
            var (requestUri, remoteEndPoint) =
                await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(_context, headers, ct).ConfigureAwait(false);

            await _context.Transport.SendRequestAsync(
                    "NOTIFY",
                    requestUri,
                    headers,
                    sipfrag,
                    remoteEndPoint,
                    _context.SignalingTransport,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.Logger.LogDebug(
                ex, "Failed to send REFER NOTIFY ({State}) on {CallId}.", subscriptionState, _context.CallId);
        }
    }
}
