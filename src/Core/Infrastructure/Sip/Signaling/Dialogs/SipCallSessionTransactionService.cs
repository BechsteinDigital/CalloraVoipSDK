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
        var nonceCount = 1;
        var authAttempted = false;
        string? authorization = null;
        string? authorizationHeaderName = null;
        var reliablePrackSync = new object();
        var highestScheduledPrackRseq = 0;
        Task reliablePrackSendChain = Task.CompletedTask;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var branch = SipProtocol.NewBranch();
            var transactionInviteCSeq = cseq;
            _context.ActiveInviteCSeq = cseq;
            _context.ActiveInviteBranch = branch;

            var headers = _headers.CreateDialogRequestHeaders(
                method: "INVITE",
                cseq: cseq,
                branch: branch,
                authorizationHeaderName: authorizationHeaderName,
                authorizationHeader: authorization,
                includeContentType: !string.IsNullOrWhiteSpace(body));
            SipSessionTimerPolicy.ApplyOutboundOfferHeaders(headers);
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
                                if (rseq <= highestScheduledPrackRseq)
                                {
                                    _context.Logger.LogDebug(
                                        "Skipping out-of-order or duplicate reliable provisional RSeq={RSeq} on {CallId}.",
                                        rseq,
                                        _context.CallId);
                                    return;
                                }

                                highestScheduledPrackRseq = rseq;
                                reliablePrackSendChain = reliablePrackSendChain
                                    .ContinueWith(
                                        _ => SendReliablePrackAsync(rackHeader, envelope.RemoteEndPoint, ct),
                                        ct,
                                        TaskContinuationOptions.None,
                                        TaskScheduler.Default)
                                    .Unwrap();
                            }
                        },
                    },
                    ct)
                .ConfigureAwait(false);

            Task prackCompletion;
            lock (reliablePrackSync)
                prackCompletion = reliablePrackSendChain;
            await prackCompletion.WaitAsync(ct).ConfigureAwait(false);

            var finalResponse = transactionResult.FinalResponse;
            _context.ApplyInviteDialogResponse(finalResponse.Response);

            _context.RemoteEndPoint = finalResponse.RemoteEndPoint;

            if (SipProtocol.IsSuccess(finalResponse.Response.StatusCode))
            {
                _context.RemoteTag = SipProtocol.ExtractTag(finalResponse.Response.Header("To")) ?? _context.RemoteTag;
                await SendAckAsync(cseq, _context.RemoteEndPoint, ct).ConfigureAwait(false);
                _context.ActiveInviteBranch = null;

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
                        out var nextAuthorizationHeaderName)
                    && _context.DigestAuthenticator.TryCreateAuthorizationHeader(
                        challengeHeader,
                        _context.AuthUsername,
                        _context.AuthPassword!,
                        "INVITE",
                        _context.RemoteRequestUri,
                        nonceCount++,
                        out var generatedAuthorization))
                {
                    authAttempted = true;
                    authorizationHeaderName = nextAuthorizationHeaderName;
                    authorization = generatedAuthorization;
                    cseq = _context.NextLocalCSeq();
                    continue;
                }
            }

            if (authAttempted
                && finalResponse.Response.StatusCode is 401 or 407
                && !string.IsNullOrWhiteSpace(_context.AuthPassword))
            {
                if (SipDigestChallengeSelector.TrySelect(
                        finalResponse.Response,
                        out var challengeHeader,
                        out var nextAuthorizationHeaderName)
                    && SipDigestChallengeSelector.IsStaleChallenge(challengeHeader)
                    && _context.DigestAuthenticator.TryCreateAuthorizationHeader(
                        challengeHeader,
                        _context.AuthUsername,
                        _context.AuthPassword!,
                        "INVITE",
                        _context.RemoteRequestUri,
                        nonceCount++,
                        out var generatedAuthorization))
                {
                    authorizationHeaderName = nextAuthorizationHeaderName;
                    authorization = generatedAuthorization;
                    cseq = _context.NextLocalCSeq();
                    continue;
                }
            }

            _context.ActiveInviteBranch = null;
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

        var transactionResult = await _clientTransactions.ExecuteAsync(
                new SipClientTransactionRequest
                {
                    Method = "UPDATE",
                    RequestUri = _context.RemoteRequestUri,
                    Headers = headers,
                    Body = null,
                    RemoteEndPoint = _context.RemoteEndPoint,
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
        if (_context.ActiveInviteCSeq <= 0 || string.IsNullOrWhiteSpace(_context.ActiveInviteBranch))
            return;

        MarkCancelledInvite(_context.ActiveInviteCSeq, reason);

        // RFC 3261 §9.1: CANCEL matches the INVITE transaction by reusing the INVITE
        // request's top Via branch. The numeric CSeq equals the INVITE CSeq; the method is CANCEL.
        var headers = _headers.CreateDialogRequestHeaders(
            method: "CANCEL",
            cseq: _context.ActiveInviteCSeq,
            branch: _context.ActiveInviteBranch,
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
        var nonceCount = 1;
        var authAttempted = false;
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

            var transactionResult = await _clientTransactions.ExecuteAsync(
                    new SipClientTransactionRequest
                    {
                        Method = method,
                        RequestUri = _context.RemoteRequestUri,
                        Headers = headers,
                        Body = body,
                        RemoteEndPoint = _context.RemoteEndPoint,
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
                    nonceCount,
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
                nonceCount++;
                continue;
            }

            if (isStaleNonce)
            {
                authorizationHeaderName = nextAuthorizationHeaderName;
                authorizationHeaderValue = nextAuthorizationHeaderValue;
                nonceCount++;
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
        int nonceCount,
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
                _context.RemoteRequestUri,
                nonceCount,
                out var generatedAuthorization))
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
    /// Sends PRACK request for one reliable provisional INVITE response.
    /// </summary>
    private async Task SendReliablePrackAsync(
        string rackHeaderValue,
        IPEndPoint remoteEndPoint,
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

        var transactionResult = await _clientTransactions.ExecuteAsync(
                new SipClientTransactionRequest
                {
                    Method = "PRACK",
                    RequestUri = _context.RemoteRequestUri,
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
    private async Task SendAckAsync(int inviteCseq, IPEndPoint remoteEndPoint, CancellationToken ct)
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

        await _context.Transport.SendRequestAsync(
                "ACK",
                _context.RemoteRequestUri,
                headers,
                body: null,
                remoteEndPoint,
                _context.SignalingTransport,
                ct)
            .ConfigureAwait(false);
    }

}
