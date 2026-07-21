using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Executes SIP transaction logic for one call session.
/// </summary>
internal sealed class SipCallSessionTransactionService
{
    private readonly ISipCallSessionContext _context;
    private readonly SipCallSessionHeaderService _headers;
    private readonly ISipClientTransactionExecutor _clientTransactions;
    private readonly SipForkedInviteHandler _forkedInvites;
    private readonly object _cancelledInviteSync = new();
    private int _cancelledInviteCSeq;
    private SipDialogTerminationReason? _cancelledInviteReason;

    // Bound stale-nonce refreshes so a peer that answers stale=true to every authenticated request
    // cannot spin an INVITE/in-dialog transaction into an unbounded retry loop (DoS). One fresh nonce
    // should suffice; this mirrors the REGISTER path (SipRegistrationService.maxStaleRetries).
    private const int MaxStaleNonceRetries = 2;

    // RFC 4028 §5: bound how many times a 422 "Session Interval Too Small" may raise the offer and retry, so a
    // misbehaving peer/proxy cannot loop the INVITE transaction.
    private const int MaxSessionTimerRetries = 2;

    /// <summary>
    /// Creates a transaction service bound to one call session context.
    /// </summary>
    public SipCallSessionTransactionService(
        ISipCallSessionContext context,
        SipCallSessionHeaderService headers)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        _clientTransactions = new SipClientTransactionExecutor(_context.Transport, _context.Logger);
        _forkedInvites = new SipForkedInviteHandler(_context);
    }

    /// <summary>
    /// Updates dialog remote endpoint from inbound responses.
    /// Client transactions subscribe to transport directly for correlation.
    /// </summary>
    public void HandleInboundResponse(IPEndPoint remoteEndPoint, SipResponse response)
    {
        if (_context.IsDisposed) return;
        if (!string.Equals(response.Header("Call-ID"), _context.CallId, StringComparison.Ordinal))
            return;
        _context.RemoteEndPoint = remoteEndPoint;
        _context.TryApplyRemoteAssertedIdentity(
            response.Header("P-Asserted-Identity"),
            remoteEndPoint);
        _forkedInvites.HandleSuccessResponse(response, remoteEndPoint);
    }

    /// <summary>
    /// Sends one INVITE transaction (initial or re-INVITE), including optional digest retry.
    /// </summary>
    public async Task SendInviteTransactionAsync(
        string? body,
        bool allowRingingTransition,
        SipDialogState successState,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_context.LocalTag))
            throw new InvalidOperationException("Local tag must be set before INVITE.");

        var cseq = _context.NextLocalCSeq();
        var nonceCounter = new SipNonceCounter();
        var authAttempted = false;
        var staleRetries = 0;
        var timerRetries = 0;
        int? sessionIntervalOverride = null;
        string? authorizationHeaderName = null;
        string? selectedChallenge = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // RFC 3262 §4 (CF-044): the reliable-provisional receipt order and the PRACK send-chain are per-INVITE
            // state — reset for each attempt so a retried INVITE (422/auth) starts a fresh RSeq sequence rather
            // than rejecting the new transaction's provisionals as out-of-order against the previous attempt.
            var reliablePrackSync = new object();
            var reliableProvisionalOrder = new SipReliableProvisionalReceiptOrder();
            Task reliablePrackSendChain = Task.CompletedTask;

            var branch = SipProtocol.NewBranch();
            var transactionInviteCSeq = cseq;
            _context.SetActiveInvite(cseq, branch);

            // RFC 7616 §3.4 (CF-047): (re)generate the Authorization header for THIS request from the selected
            // challenge, so every request that reuses the nonce carries a fresh, incrementing nonce-count (nc).
            // The loop stores the selected challenge, not the finished Authorization line — replaying that line
            // reuses the same nc, which a strict server rejects as a replay (e.g. a 422 Min-SE retry after an
            // already-authenticated INVITE). One NextFor call per send keeps the nc unique per request.
            string? authorization = null;
            if (selectedChallenge is not null
                && _context.DigestAuthenticator.TryCreateAuthorizationHeader(
                    selectedChallenge,
                    _context.AuthUsername,
                    _context.AuthPassword!,
                    "INVITE",
                    _context.RemoteRequestUri,
                    nonceCounter.NextFor(selectedChallenge),
                    out var regeneratedAuthorization,
                    body: body))
            {
                authorization = regeneratedAuthorization;
            }

            var headers = _headers.CreateDialogRequestHeaders(
                method: "INVITE",
                cseq: cseq,
                branch: branch,
                authorizationHeaderName: authorizationHeaderName,
                authorizationHeader: authorization,
                includeContentType: !string.IsNullOrWhiteSpace(body));
            SipSessionTimerPolicy.ApplyOutboundOfferHeaders(headers, sessionIntervalOverride);
            headers["Supported"] = SipCallSessionTransactionUtilities.AppendSupportedToken(headers.TryGetValue("Supported", out var supportedValue) ? supportedValue : null, "100rel");

            var transactionResult = await _clientTransactions.ExecuteAsync(
                    new SipClientTransactionRequest
                    {
                        Method = "INVITE",
                        RequestUri = _context.RemoteRequestUri,
                        Headers = headers,
                        Body = body,
                        RemoteEndPoint = _context.RemoteEndPoint,
                        Transport = _context.SignalingTransport,
                        Timeout = _context.Timeout,
                        OnProvisionalResponse = envelope =>
                        {
                            _context.ApplyInviteDialogResponse(envelope.Response);
                            _context.RemoteEndPoint = envelope.RemoteEndPoint;
                            if (allowRingingTransition && envelope.Response.StatusCode is 180 or 183)
                                _context.TransitionTo(SipDialogState.Ringing);

                            if (!TryBuildReliablePrackHeader(envelope.Response, transactionInviteCSeq, out var rseq, out var rackHeader))
                                return;

                            lock (reliablePrackSync)
                            {
                                // RFC 3262 §4 (CF-044): PRACK only the next in-order reliable provisional. A gap
                                // (higher RSeq than expected) or a duplicate/older RSeq is not acknowledged — the
                                // UAC waits for the retransmission of the missing response.
                                if (!reliableProvisionalOrder.TryAcceptInOrder(rseq))
                                {
                                    _context.Logger.LogDebug(
                                        "Skipping out-of-order or duplicate reliable provisional RSeq={RSeq} on {CallId}.",
                                        rseq,
                                        _context.CallId);
                                    return;
                                }

                                // Chain by AWAITING the predecessor (not ContinueWith, which runs regardless of a
                                // faulted/cancelled antecedent and drops its exception): a failed earlier PRACK
                                // then propagates through the chain and aborts the INVITE at the await below,
                                // instead of being silently swallowed (CF-044).
                                reliablePrackSendChain = SendReliablePrackAfterAsync(
                                    reliablePrackSendChain, rackHeader, envelope.RemoteEndPoint, ct);
                            }
                        },
                    },
                    ct)
                .ConfigureAwait(false);

            Task prackCompletion;
            lock (reliablePrackSync)
                prackCompletion = reliablePrackSendChain;
            try
            {
                await prackCompletion.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // A failed or cancelled reliable-PRACK chain aborts the INVITE while the dialog may
                // still be in Ringing and therefore cancellable. Clear the active INVITE state so a
                // later CANCEL cannot fire against this abandoned transaction (HARD-C2).
                _context.ClearActiveInvite();
                throw;
            }

            var finalResponse = transactionResult.FinalResponse;
            _context.ApplyInviteDialogResponse(finalResponse.Response);

            _context.RemoteEndPoint = finalResponse.RemoteEndPoint;

            if (SipProtocol.IsSuccess(finalResponse.Response.StatusCode))
            {
                _context.RemoteTag = SipProtocol.ExtractTag(finalResponse.Response.Header("To")) ?? _context.RemoteTag;
                // A 2xx makes the INVITE non-cancellable; clear before the ACK so a failing ACK send
                // cannot leave a stale CANCEL target once the dialog becomes Established (HARD-C2).
                _context.ClearActiveInvite();
                await SendAckAsync(cseq, ct).ConfigureAwait(false);

                if (TryConsumeCancelledInvite(cseq, out var cancelledInviteReason))
                {
                    await SendByeAsync(CancellationToken.None, cancelledInviteReason).ConfigureAwait(false);
                    _context.TransitionTo(SipDialogState.Terminated, cancelledInviteReason);
                    return;
                }

                // For outbound calls, the 200 OK body is the remote SDP answer.
                if (!string.IsNullOrWhiteSpace(finalResponse.Response.Body))
                    _context.SetRemoteSdp(finalResponse.Response.Body);
                _context.ApplySessionTimerNegotiation(
                    finalResponse.Response.Header("Session-Expires"),
                    localIsRequester: true);
                _context.TransitionTo(successState);
                return;
            }

            // RFC 3261 §17.1.1.3: ACK for 3xx-6xx INVITE responses is sent automatically
            // by the client transaction (SipClientTransactionExecutor) with the same Via branch.
            // The TU must NOT send a duplicate ACK here.
            _context.RemoteTag = SipProtocol.ExtractTag(finalResponse.Response.Header("To")) ?? _context.RemoteTag;

            if (!authAttempted
                && finalResponse.Response.StatusCode is 401 or 407
                && !string.IsNullOrWhiteSpace(_context.AuthPassword))
            {
                if (SipDigestChallengeSelector.TrySelect(
                        finalResponse.Response,
                        out var challengeHeader,
                        out var nextAuthorizationHeaderName))
                {
                    // Store the challenge; the Authorization line is (re)built at the loop top with a fresh nc.
                    authAttempted = true;
                    authorizationHeaderName = nextAuthorizationHeaderName;
                    selectedChallenge = challengeHeader;
                    cseq = _context.NextLocalCSeq();
                    continue;
                }
            }

            if (authAttempted
                && staleRetries < MaxStaleNonceRetries
                && finalResponse.Response.StatusCode is 401 or 407
                && !string.IsNullOrWhiteSpace(_context.AuthPassword))
            {
                if (SipDigestChallengeSelector.TrySelect(
                        finalResponse.Response,
                        out var challengeHeader,
                        out var nextAuthorizationHeaderName)
                    && SipDigestChallengeSelector.IsStaleChallenge(challengeHeader))
                {
                    // A fresh nonce: store it; NextFor resets the nc to 1 for the new nonce at the loop top.
                    staleRetries++;
                    authorizationHeaderName = nextAuthorizationHeaderName;
                    selectedChallenge = challengeHeader;
                    cseq = _context.NextLocalCSeq();
                    continue;
                }
            }
            else if (authAttempted
                && staleRetries >= MaxStaleNonceRetries
                && finalResponse.Response.StatusCode is 401 or 407)
            {
                _context.Logger.LogWarning(
                    "SIP session {CallId}: peer issued more than {Max} stale-nonce INVITE challenges; abandoning digest retry.",
                    _context.CallId, MaxStaleNonceRetries);
            }

            // RFC 4028 §5/§6: a 422 "Session Interval Too Small" carries the peer/proxy Min-SE. Retry the INVITE
            // with the offered Session-Expires (and our Min-SE) raised to at least that value; bounded so a
            // misbehaving peer that keeps rejecting cannot loop the transaction indefinitely.
            if (finalResponse.Response.StatusCode == 422
                && timerRetries < MaxSessionTimerRetries
                && SipSessionTimerPolicy.TryParseMinSe(finalResponse.Response.Header("Min-SE"), out var requiredMinSe)
                && requiredMinSe > (sessionIntervalOverride ?? SipSessionTimerPolicy.DefaultSessionExpiresSeconds))
            {
                timerRetries++;
                sessionIntervalOverride = requiredMinSe;
                cseq = _context.NextLocalCSeq();
                _context.Logger.LogDebug(
                    "SIP session {CallId}: 422 Session Interval Too Small; retrying INVITE with Session-Expires {MinSe}s.",
                    _context.CallId, requiredMinSe);
                continue;
            }

            _context.ClearActiveInvite();
            ClearCancelledInvite(cseq);

            // RFC 3261 §20.33 / RFC 7339 §5.3: parse Retry-After from 503 responses.
            int? retryAfterSeconds = null;
            var retryAfterRaw = finalResponse.Response.Header("Retry-After");
            if (!string.IsNullOrWhiteSpace(retryAfterRaw)
                && finalResponse.Response.StatusCode == 503)
            {
                if (int.TryParse(retryAfterRaw.Split(';')[0].Trim(), out var retryAfterSec))
                {
                    retryAfterSeconds = retryAfterSec;
                    _context.Logger.LogInformation(
                        "SIP session {CallId}: 503 Service Unavailable with Retry-After={RetryAfterSeconds}s.",
                        _context.CallId, retryAfterSec);
                }
            }

            // RFC 3261 §21.4.26: 488 Not Acceptable Here indicates SDP negotiation failure —
            // use a dedicated termination reason so callers can distinguish media errors from
            // call rejections (486/603) or auth failures.
            var terminationReason = finalResponse.Response.StatusCode == 488
                ? new SipDialogTerminationReason("SIP", cause: 488, text: "Not Acceptable Here")
                : SipCallSessionTransactionUtilities.ResolveTerminationReason(
                    finalResponse.Response.Header("Reason"),
                    finalResponse.Response.StatusCode,
                    finalResponse.Response.ReasonPhrase,
                    retryAfterSeconds);

            _context.TransitionTo(SipDialogState.Terminated, terminationReason);
            throw new SipFinalResponseException(
                $"INVITE failed with status {finalResponse.Response.StatusCode} {finalResponse.Response.ReasonPhrase}.",
                finalResponse);
        }
    }

    /// <summary>
    /// Sends in-dialog SIP INFO and validates success response.
    /// </summary>
    public async Task SendInfoAsync(
        string contentType,
        string body,
        CancellationToken ct)
    {
        var finalResponse = await SendInDialogRequestAsync(
                method: "INFO",
                body: body,
                contentType: contentType,
                extraHeaders: null,
                ct)
            .ConfigureAwait(false);

        if (!SipProtocol.IsSuccess(finalResponse.Response.StatusCode))
        {
            throw new InvalidOperationException(
                $"INFO failed with status {finalResponse.Response.StatusCode} {finalResponse.Response.ReasonPhrase}.");
        }
    }

    /// <summary>
    /// Sends in-dialog SIP REFER for transfer and returns true when accepted.
    /// </summary>
    public async Task<bool> SendReferAsync(
        string referTo,
        string? referredBy,
        bool suppressSubscription,
        CancellationToken ct)
    {
        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Refer-To"] = referTo
        };
        if (suppressSubscription)
        {
            // RFC 4488: ask remote not to create an implicit NOTIFY subscription.
            extraHeaders["Refer-Sub"] = "false";
            extraHeaders["Require"] = "norefersub";
        }
        if (!string.IsNullOrWhiteSpace(referredBy))
            extraHeaders["Referred-By"] = referredBy;

        var finalResponse = await SendInDialogRequestAsync(
                method: "REFER",
                body: null,
                contentType: null,
                extraHeaders,
                ct)
            .ConfigureAwait(false);

        return SipProtocol.IsSuccess(finalResponse.Response.StatusCode);
    }

    /// <summary>
    /// Sends in-dialog SIP OPTIONS and returns true on 2xx.
    /// </summary>
    public async Task<bool> SendOptionsAsync(CancellationToken ct)
    {
        var finalResponse = await SendInDialogRequestAsync(
                method: "OPTIONS",
                body: null,
                contentType: null,
                extraHeaders: null,
                ct)
            .ConfigureAwait(false);

        return SipProtocol.IsSuccess(finalResponse.Response.StatusCode);
    }

    /// <summary>
    /// Sends in-dialog SIP SUBSCRIBE and returns true on 2xx acceptance.
    /// </summary>
    public async Task<bool> SendSubscribeAsync(
        string eventType,
        int expiresSeconds,
        string? acceptHeader,
        string? body,
        CancellationToken ct)
    {
        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = eventType,
            ["Expires"] = Math.Max(0, expiresSeconds).ToString()
        };
        if (!string.IsNullOrWhiteSpace(acceptHeader))
            extraHeaders["Accept"] = acceptHeader;

        var finalResponse = await SendInDialogRequestAsync(
                method: "SUBSCRIBE",
                body: body,
                contentType: null,
                extraHeaders,
                ct)
            .ConfigureAwait(false);

        return SipProtocol.IsSuccess(finalResponse.Response.StatusCode)
            || finalResponse.Response.StatusCode == 202;
    }

    /// <summary>
    /// Sends in-dialog NOTIFY for an active subscription (RFC 6665 §4.2.2).
    /// </summary>
    public async Task<bool> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType,
        string? body,
        CancellationToken ct)
    {
        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = eventType,
            ["Subscription-State"] = subscriptionState
        };

        var finalResponse = await SendInDialogRequestAsync(
                method: "NOTIFY",
                body: body,
                contentType: contentType,
                extraHeaders,
                ct)
            .ConfigureAwait(false);

        return SipProtocol.IsSuccess(finalResponse.Response.StatusCode);
    }

    /// <summary>
    /// Sends BYE and waits for final non-provisional response.
    /// </summary>
    public async Task SendByeAsync(
        CancellationToken ct,
        SipDialogTerminationReason? reason = null)
    {
        try
        {
            await SendInDialogRequestAsync(
                    method: "BYE",
                    body: null,
                    contentType: null,
                    extraHeaders: SipCallSessionTransactionUtilities.CreateReasonHeaders(reason),
                    ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _context.Logger.LogDebug(ex, "Timed out waiting for BYE response on {CallId}.", _context.CallId);
        }
    }

    /// <summary>
    /// Sends in-dialog UPDATE as RFC4028 session refresh and returns true when accepted.
    /// </summary>
    public async Task<bool> SendSessionRefreshUpdateAsync(CancellationToken ct)
    {
        var cseq = _context.NextLocalCSeq();
        var headers = _headers.CreateDialogRequestHeaders(
            method: "UPDATE",
            cseq: cseq,
            branch: SipProtocol.NewBranch(),
            authorizationHeaderName: null,
            authorizationHeader: null,
            includeContentType: false);
        SipSessionTimerPolicy.ApplyOutboundOfferHeaders(headers);

        // RFC 3261 §12.2.1.1 (CF-014): route the in-dialog UPDATE via the dialog route set / topmost route.
        var (requestUri, remoteEndPoint) =
            await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(_context, headers, ct).ConfigureAwait(false);

        var transactionResult = await _clientTransactions.ExecuteAsync(
                new SipClientTransactionRequest
                {
                    Method = "UPDATE",
                    RequestUri = requestUri,
                    Headers = headers,
                    Body = null,
                    RemoteEndPoint = remoteEndPoint,
                    Transport = _context.SignalingTransport,
                    Timeout = _context.Timeout
                },
                ct)
            .ConfigureAwait(false);

        var response = transactionResult.FinalResponse;
        _context.RemoteEndPoint = response.RemoteEndPoint;
        _context.ApplyTargetRefreshDialogResponse(response.Response, "UPDATE");
        if (!SipProtocol.IsSuccess(response.Response.StatusCode))
            return false;

        _context.ApplySessionTimerNegotiation(
            response.Response.Header("Session-Expires"),
            localIsRequester: true);
        return true;
    }

    /// <summary>
    /// Sends CANCEL for active outbound INVITE.
    /// </summary>
    public async Task SendCancelAsync(
        CancellationToken ct,
        SipDialogTerminationReason? reason = null)
    {
        // Snapshot CSeq+branch as one atomic pair: the INVITE loop clears both together when the
        // transaction completes, so reading the two properties separately could build a CANCEL with
        // a live CSeq and a null/stale branch (or vice versa) (HARD-C2).
        var (activeCSeq, activeBranch) = _context.ActiveInvite;
        if (activeCSeq <= 0 || string.IsNullOrWhiteSpace(activeBranch))
            return;

        MarkCancelledInvite(activeCSeq, reason);

        // RFC 3261 §9.1: CANCEL matches the INVITE transaction by reusing the INVITE
        // request's top Via branch. The numeric CSeq equals the INVITE CSeq; the method is CANCEL.
        var headers = _headers.CreateDialogRequestHeaders(
            method: "CANCEL",
            cseq: activeCSeq,
            branch: activeBranch,
            authorizationHeaderName: null,
            authorizationHeader: null,
            includeContentType: false);
        SipCallSessionTransactionUtilities.AppendReasonHeader(headers, reason);

        await _clientTransactions.ExecuteAsync(
                new SipClientTransactionRequest
                {
                    Method = "CANCEL",
                    RequestUri = _context.RemoteRequestUri,
                    Headers = headers,
                    Body = null,
                    RemoteEndPoint = _context.RemoteEndPoint,
                    Transport = _context.SignalingTransport,
                    Timeout = _context.Timeout
                },
                ct)
            .ConfigureAwait(false);
    }

    private void MarkCancelledInvite(
        int inviteCseq,
        SipDialogTerminationReason? reason)
    {
        lock (_cancelledInviteSync)
        {
            _cancelledInviteCSeq = inviteCseq;
            _cancelledInviteReason = reason;
        }
    }

    private bool TryConsumeCancelledInvite(
        int inviteCseq,
        out SipDialogTerminationReason? reason)
    {
        lock (_cancelledInviteSync)
        {
            if (_cancelledInviteCSeq != inviteCseq)
            {
                reason = null;
                return false;
            }

            reason = _cancelledInviteReason;
            _cancelledInviteCSeq = 0;
            _cancelledInviteReason = null;
            return true;
        }
    }

    private void ClearCancelledInvite(int inviteCseq)
    {
        lock (_cancelledInviteSync)
        {
            if (_cancelledInviteCSeq != inviteCseq)
                return;

            _cancelledInviteCSeq = 0;
            _cancelledInviteReason = null;
        }
    }

    /// <summary>
    /// Sends one generic in-dialog request and waits for final non-provisional response.
    /// </summary>
    private async Task<SipResponseEnvelope> SendInDialogRequestAsync(
        string method,
        string? body,
        string? contentType,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        var nonceCounter = new SipNonceCounter();
        var authAttempted = false;
        var staleRetries = 0;
        string? authorizationHeaderName = null;
        string? authorizationHeaderValue = null;

        while (true)
        {
            var cseq = _context.NextLocalCSeq();
            var headers = _headers.CreateDialogRequestHeaders(
                method: method,
                cseq: cseq,
                branch: SipProtocol.NewBranch(),
                authorizationHeaderName: authorizationHeaderName,
                authorizationHeader: authorizationHeaderValue,
                includeContentType: false);
            if (!string.IsNullOrWhiteSpace(contentType))
                headers["Content-Type"] = contentType;
            if (extraHeaders is not null)
            {
                foreach (var header in extraHeaders)
                    headers[header.Key] = header.Value;
            }

            // RFC 3261 §12.2.1.1 (CF-014): compose the Request-URI + Route header from the dialog route set
            // (incl. the strict-router rewrite) and resolve the transport next hop from the topmost route,
            // instead of sending straight to the last response's source socket.
            var (requestUri, remoteEndPoint) =
                await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(_context, headers, ct).ConfigureAwait(false);

            var transactionResult = await _clientTransactions.ExecuteAsync(
                    new SipClientTransactionRequest
                    {
                        Method = method,
                        RequestUri = requestUri,
                        Headers = headers,
                        Body = body,
                        RemoteEndPoint = remoteEndPoint,
                        Transport = _context.SignalingTransport,
                        Timeout = _context.Timeout
                    },
                    ct)
                .ConfigureAwait(false);

            var response = transactionResult.FinalResponse;
            _context.RemoteEndPoint = response.RemoteEndPoint;
            _context.ApplyTargetRefreshDialogResponse(response.Response, method);
            if (SipProtocol.IsSuccess(response.Response.StatusCode)
                && (method.Equals("INVITE", StringComparison.Ordinal)
                    || method.Equals("UPDATE", StringComparison.Ordinal)))
            {
                _context.ApplySessionTimerNegotiation(
                    response.Response.Header("Session-Expires"),
                    localIsRequester: true);
            }

            if (!TryResolveInDialogAuthRetry(
                    response.Response,
                    method,
                    body,
                    requestUri,
                    nonceCounter,
                    authAttempted,
                    out var shouldRetryWithAuth,
                    out var isStaleNonce,
                    out var nextAuthorizationHeaderName,
                    out var nextAuthorizationHeaderValue))
            {
                return response;
            }

            if (shouldRetryWithAuth)
            {
                authAttempted = true;
                authorizationHeaderName = nextAuthorizationHeaderName;
                authorizationHeaderValue = nextAuthorizationHeaderValue;
                continue;
            }

            if (isStaleNonce)
            {
                if (staleRetries >= MaxStaleNonceRetries)
                {
                    _context.Logger.LogWarning(
                        "SIP session {CallId}: peer issued more than {Max} stale-nonce {Method} challenges; abandoning digest retry.",
                        _context.CallId, MaxStaleNonceRetries, method);
                    return response;
                }

                staleRetries++;
                authorizationHeaderName = nextAuthorizationHeaderName;
                authorizationHeaderValue = nextAuthorizationHeaderValue;
                continue;
            }

            return response;
        }
    }

    /// <summary>
    /// Resolves whether one in-dialog request should retry with digest authentication.
    /// </summary>
    private bool TryResolveInDialogAuthRetry(
        SipResponse response,
        string method,
        string? body,
        string effectiveRequestUri,
        SipNonceCounter nonceCounter,
        bool authAttempted,
        out bool shouldRetryWithAuth,
        out bool isStaleNonceRetry,
        out string authorizationHeaderName,
        out string authorizationHeaderValue)
    {
        shouldRetryWithAuth = false;
        isStaleNonceRetry = false;
        authorizationHeaderName = string.Empty;
        authorizationHeaderValue = string.Empty;

        if (response.StatusCode is not (401 or 407))
            return false;
        if (string.IsNullOrWhiteSpace(_context.AuthPassword))
            return false;
        if (!SipDigestChallengeSelector.TrySelect(
                response,
                out var challengeHeader,
                out var nextAuthorizationHeaderName))
        {
            return false;
        }
        if (!_context.DigestAuthenticator.TryCreateAuthorizationHeader(
                challengeHeader,
                _context.AuthUsername,
                _context.AuthPassword!,
                method,
                // RFC 3261 §22.4 / RFC 2617: digest-uri-value is the request's Request-URI. On a strict-router
                // rewrite the wire Request-URI is the topmost route, not the dialog remote target, so the digest
                // must sign the effective Request-URI from the routing plan (computed before auth) — otherwise the
                // server recomputes over a different URI and the retry is rejected. Loose/direct dialogs leave
                // this equal to the remote target, so their digest is unchanged (CF-014 finding).
                effectiveRequestUri,
                nonceCounter.NextFor(challengeHeader),
                out var generatedAuthorization,
                body: body))
        {
            return false;
        }

        authorizationHeaderName = nextAuthorizationHeaderName;
        authorizationHeaderValue = generatedAuthorization;

        if (!authAttempted)
        {
            shouldRetryWithAuth = true;
            return true;
        }

        if (SipDigestChallengeSelector.IsStaleChallenge(challengeHeader))
        {
            isStaleNonceRetry = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Chains one reliable PRACK after its predecessor in the send-order chain: awaits the predecessor first so a
    /// prior PRACK failure or cancellation propagates (rather than being swallowed by a fire-and-forget
    /// continuation), then sends this PRACK. Preserves strict in-order PRACK delivery (RFC 3262 §4, CF-044).
    /// </summary>
    private async Task SendReliablePrackAfterAsync(
        Task predecessor,
        string rackHeaderValue,
        IPEndPoint provisionalSource,
        CancellationToken ct)
    {
        await predecessor.ConfigureAwait(false);
        await SendReliablePrackAsync(rackHeaderValue, provisionalSource, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends PRACK request for one reliable provisional INVITE response.
    /// </summary>
    private async Task SendReliablePrackAsync(
        string rackHeaderValue,
        IPEndPoint provisionalSource,
        CancellationToken ct)
    {
        var cseq = _context.NextLocalCSeq();
        var headers = _headers.CreateDialogRequestHeaders(
            method: "PRACK",
            cseq: cseq,
            branch: SipProtocol.NewBranch(),
            authorizationHeaderName: null,
            authorizationHeader: null,
            includeContentType: false);
        headers["RAck"] = rackHeaderValue;
        headers["Supported"] = SipCallSessionTransactionUtilities.AppendSupportedToken(headers.TryGetValue("Supported", out var existingSupported) ? existingSupported : null, "100rel");

        // RFC 3262 §4 + RFC 3261 §12.2.1.1 (CF-014): PRACK is an in-dialog request within the early dialog and
        // follows its route set / topmost route; a direct early dialog is pinned to the exact provisional this
        // PRACK answers (so a later provisional cannot shift its destination).
        var (requestUri, remoteEndPoint) =
            await SipInDialogRequestRouting
                .ApplyInDialogRoutingAsync(_context, headers, ct, provisionalSource)
                .ConfigureAwait(false);

        var transactionResult = await _clientTransactions.ExecuteAsync(
                new SipClientTransactionRequest
                {
                    Method = "PRACK",
                    RequestUri = requestUri,
                    Headers = headers,
                    Body = null,
                    RemoteEndPoint = remoteEndPoint,
                    Transport = _context.SignalingTransport,
                    Timeout = _context.Timeout
                },
                ct)
            .ConfigureAwait(false);

        if (!SipProtocol.IsSuccess(transactionResult.FinalResponse.Response.StatusCode))
        {
            throw new InvalidOperationException(
                $"PRACK failed with status {transactionResult.FinalResponse.Response.StatusCode} {transactionResult.FinalResponse.Response.ReasonPhrase}.");
        }
    }

    /// <summary>
    /// Tries to build RAck header value for reliable provisional response acknowledgment.
    /// </summary>
    private static bool TryBuildReliablePrackHeader(
        SipResponse response,
        int inviteCseq,
        out int rseq,
        out string rackHeaderValue)
    {
        rseq = 0;
        rackHeaderValue = string.Empty;
        if (!SipProtocol.IsProvisional(response.StatusCode) || response.StatusCode == 100)
            return false;

        var rseqHeader = response.Header("RSeq");
        if (!int.TryParse(rseqHeader, out rseq) || rseq <= 0)
            return false;

        var requireHeader = response.Header("Require") ?? string.Empty;
        if (!ProtocolCommonUtilities.ContainsToken(requireHeader, "100rel"))
            return false;

        rackHeaderValue = $"{rseq} {inviteCseq} INVITE";
        return true;
    }

    /// <summary>
    /// Sends ACK for a completed INVITE transaction.
    /// </summary>
    private async Task SendAckAsync(int inviteCseq, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_context.LocalTag))
            throw new InvalidOperationException("Local tag is missing.");

        var headers = _headers.CreateDialogRequestHeaders(
            method: "ACK",
            cseq: inviteCseq,
            branch: SipProtocol.NewBranch(),
            authorizationHeaderName: null,
            authorizationHeader: null,
            includeContentType: false);

        // RFC 3261 §13.2.2.4 + §12.2.1.1 (CF-014): the 2xx ACK follows the dialog route set and is sent to the
        // resolved topmost route (or the learned response source for a direct dialog), not blindly to the last
        // response's source socket.
        var (requestUri, remoteEndPoint) =
            await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(_context, headers, ct).ConfigureAwait(false);

        await _context.Transport.SendRequestAsync(
                "ACK",
                requestUri,
                headers,
                body: null,
                remoteEndPoint,
                _context.SignalingTransport,
                ct)
            .ConfigureAwait(false);
    }

}
