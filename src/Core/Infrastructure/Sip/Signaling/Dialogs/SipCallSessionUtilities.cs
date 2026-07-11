using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

internal static class SipCallSessionUtilities
{
    public static bool IsValidDtmfDigit(char c) =>
        c is >= '0' and <= '9' or '*' or '#' or 'A' or 'a' or 'B' or 'b' or 'C' or 'c' or 'D' or 'd';

    public static string ResolveDefaultReasonPhrase(int statusCode)
    {
        return statusCode switch
        {
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            408 => "Request Timeout",
            480 => "Temporarily Unavailable",
            481 => "Call/Transaction Does Not Exist",
            486 => "Busy Here",
            487 => "Request Terminated",
            488 => "Not Acceptable Here",
            500 => "Server Internal Error",
            503 => "Service Unavailable",
            600 => "Busy Everywhere",
            603 => "Decline",
            604 => "Does Not Exist Anywhere",
            _ => "Request Failed"
        };
    }

    public static bool ShouldUseReliableProvisional(SipRequest invite)
    {
        // RFC 3262 §3: reliable provisionals are mandatory only when the INVITE REQUIRES
        // 100rel. With merely "Supported: 100rel" they are optional — and answering with
        // Require: 100rel then blocks the final 200 OK behind PRACK retransmit timeouts
        // when the caller (e.g. Fritz!Box) never sends PRACK: the phone keeps ringing
        // although the callee accepted. So opt in only on an explicit Require.
        var require = invite.Header("Require");
        return !string.IsNullOrWhiteSpace(require)
            && ProtocolCommonUtilities.ContainsToken(require, "100rel");
    }

    public static async Task<bool> SendReliableProvisionalAndWaitForPrackAsync(
        SipRequest invite,
        string localTag,
        string callId,
        SipReliableProvisionalManager reliableProvisionalManager,
        SipCallSessionHeaderService headerService,
        ISipServerTransactionEngine serverTransactions,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol signalingTransport,
        ILogger logger,
        TimeSpan timeout,
        TimeSpan reliableProvisionalT1,
        TimeSpan reliableProvisionalT2,
        CancellationToken ct)
    {
        try
        {
            var inviteCseq = SipProtocol.ExtractCSeqNumber(invite.Header("CSeq"));
            if (inviteCseq <= 0)
            {
                logger.LogWarning("SIP session {CallId}: INVITE CSeq invalid for reliable provisional flow.", callId);
                return false;
            }

            var rseq = reliableProvisionalManager.RegisterPendingInviteProvisional(inviteCseq);
            var provisionalHeaders = headerService.CreateResponseHeadersFromRequest(
                invite,
                localTag,
                includeContentType: false);
            provisionalHeaders["Require"] = "100rel";
            provisionalHeaders["RSeq"] = rseq.ToString();

            await serverTransactions.SendResponseAsync(
                    invite,
                    remoteEndPoint,
                    signalingTransport,
                    statusCode: 180,
                    reasonPhrase: "Ringing",
                    provisionalHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);

            var prackWaitTask = reliableProvisionalManager.WaitForPrackAsync(rseq, timeout, ct);

            if (signalingTransport == SipTransportProtocol.Udp)
            {
                var retransmitDelay = reliableProvisionalT1;
                while (!prackWaitTask.IsCompleted)
                {
                    await Task.Delay(retransmitDelay, ct).ConfigureAwait(false);
                    if (prackWaitTask.IsCompleted)
                        break;

                    await serverTransactions.SendResponseAsync(
                            invite,
                            remoteEndPoint,
                            signalingTransport,
                            statusCode: 180,
                            reasonPhrase: "Ringing",
                            provisionalHeaders,
                            body: null,
                            ct)
                        .ConfigureAwait(false);

                    retransmitDelay = TimeSpan.FromMilliseconds(
                        Math.Min(reliableProvisionalT2.TotalMilliseconds, retransmitDelay.TotalMilliseconds * 2));
                }
            }

            var prackReceived = await prackWaitTask.ConfigureAwait(false);
            if (prackReceived)
                return true;

            var timeoutHeaders = headerService.CreateResponseHeadersFromRequest(
                invite,
                localTag,
                includeContentType: false);
            await serverTransactions.SendResponseAsync(
                    invite,
                    remoteEndPoint,
                    signalingTransport,
                    statusCode: 504,
                    reasonPhrase: "Server Time-out",
                    timeoutHeaders,
                    body: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SIP session {CallId}: reliable provisional handshake failed.", callId);
            return false;
        }
    }

    public static async Task<bool> SendSessionRefreshAsync(
        SemaphoreSlim operationGate,
        Func<bool> isDisposed,
        Func<SipDialogState> getState,
        Func<CancellationToken, Task<bool>> sendSessionRefreshUpdateAsync,
        Action releaseOperationGateSafe,
        string callId,
        ILogger logger,
        CancellationToken ct)
    {
        if (isDisposed())
            return false;

        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (getState() is not (SipDialogState.Established or SipDialogState.OnHold))
                return false;

            return await sendSessionRefreshUpdateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SIP session {CallId}: session refresh UPDATE failed.", callId);
            return false;
        }
        finally
        {
            releaseOperationGateSafe();
        }
    }

    public static async Task HandleSessionTimerExpiredAsync(
        SemaphoreSlim operationGate,
        Func<bool> isDisposed,
        Func<SipDialogState> getState,
        Func<CancellationToken, Task> sendByeAsync,
        Action<SipDialogState, SipDialogTerminationReason?> transitionTo,
        Action releaseOperationGateSafe,
        string callId,
        ILogger logger,
        CancellationToken ct)
    {
        if (isDisposed())
            return;

        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (getState() == SipDialogState.Terminated)
                return;

            logger.LogWarning("SIP session {CallId}: session timer expired, terminating dialog.", callId);
            if (getState() is SipDialogState.Established or SipDialogState.OnHold)
                await sendByeAsync(CancellationToken.None).ConfigureAwait(false);

            transitionTo(
                SipDialogState.Terminated,
                SipReasonHeader.CreateSipStatusReason(408, "Request Timeout"));
        }
        catch (OperationCanceledException)
        {
            // expected during disposal/shutdown
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SIP session {CallId}: session-expiry termination failed.", callId);
            transitionTo(
                SipDialogState.Terminated,
                SipReasonHeader.CreateSipStatusReason(500, "Server Internal Error"));
        }
        finally
        {
            releaseOperationGateSafe();
        }
    }

    public static bool TryValidateInboundCSeq(
        object sync,
        ref int lastRemoteCSeq,
        ref bool hasRemoteCSeq,
        SipRequest request,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase,
        out int? retryAfterSeconds)
    {
        rejectionStatusCode = 0;
        rejectionReasonPhrase = string.Empty;
        retryAfterSeconds = null;
        var method = request.Method.Trim().ToUpperInvariant();
        if (method == "ACK")
            return true;

        var cseq = SipProtocol.ExtractCSeqNumber(request.Header("CSeq"));
        if (cseq <= 0)
        {
            rejectionStatusCode = 400;
            rejectionReasonPhrase = "Bad Request";
            return false;
        }

        lock (sync)
        {
            if (method == "CANCEL")
            {
                if (hasRemoteCSeq && cseq != lastRemoteCSeq)
                {
                    rejectionStatusCode = 481;
                    rejectionReasonPhrase = "Call/Transaction Does Not Exist";
                    return false;
                }

                return true;
            }

            if (!hasRemoteCSeq)
            {
                lastRemoteCSeq = cseq;
                hasRemoteCSeq = true;
                return true;
            }

            if (cseq <= lastRemoteCSeq)
            {
                rejectionStatusCode = 500;
                rejectionReasonPhrase = "Server Internal Error";
                if (method == "INVITE")
                    retryAfterSeconds = 1;
                return false;
            }

            lastRemoteCSeq = cseq;
            return true;
        }
    }

    public static void ApplyRemoteAssertedIdentity(
        ISipIdentityTrustPolicy identityTrustPolicy,
        SipTransportProtocol signalingTransport,
        object sync,
        ref string? remoteAssertedIdentity,
        string? assertedIdentityHeader,
        IPEndPoint remoteEndPoint,
        ILogger logger,
        string callId)
    {
        try
        {
            if (!identityTrustPolicy.IsTrusted(remoteEndPoint, signalingTransport))
                return;
            if (!SipAssertedIdentityHeader.TryParseFirstIdentityUri(assertedIdentityHeader, out var identityUri))
                return;

            lock (sync)
            {
                remoteAssertedIdentity = identityUri;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to apply asserted identity for SIP session {CallId}.",
                callId);
        }
    }

    /// <summary>
    /// Extracts the first URI from a <c>Diversion</c> header value (RFC 5806), stripping the
    /// name-addr angle brackets and any header parameters (reason, counter, ...). Returns
    /// <see langword="null"/> when the header is absent or unparsable. Diversion is informational
    /// routing history and is surfaced as-received (no trust gating).
    /// </summary>
    public static string? ParseDiversionUri(string? diversionHeader)
        => SipAssertedIdentityHeader.TryParseFirstIdentityUri(diversionHeader, out var uri)
            ? uri
            : null;
}
