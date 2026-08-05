using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;

/// <summary>
/// Default SIP client transaction executor with RFC3261-style retransmission behavior
/// for UDP and response correlation by Call-ID/CSeq/Via branch.
/// </summary>
internal sealed class SipClientTransactionExecutor : ISipClientTransactionExecutor
{
    private static readonly TimeSpan DefaultTransactionTimeout = TimeSpan.FromSeconds(32);
    private static readonly TimeSpan DefaultInviteFailureCompletedRetention = TimeSpan.FromSeconds(32);
    private static readonly TimeSpan DefaultNonInviteCompletedRetention = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upper bound on provisional (1xx) responses retained for one transaction (#158 P1-8). The provisional
    /// history is neither de-duplicated nor count-capped otherwise, so a peer or forking proxy emitting an
    /// unbounded stream of 1xx within the transaction window would grow it without limit. Excess provisionals
    /// are still dispatched to the per-response callback (so early media / early dialogs keep working, bounded
    /// downstream), only the retained snapshot is capped.
    /// </summary>
    private const int MaxProvisionalResponses = 64;

    private readonly ISipTransportRuntime _transport;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a transaction executor for one transport runtime.
    /// </summary>
    public SipClientTransactionExecutor(
        ISipTransportRuntime transport,
        ILogger logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SipClientTransactionResult> ExecuteAsync(
        SipClientTransactionRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);

        var method = request.Method.Trim().ToUpperInvariant();
        var isInvite = string.Equals(method, "INVITE", StringComparison.Ordinal);
        var callId = request.Headers["Call-ID"];
        var requestCSeq = SipProtocol.ExtractCSeqNumber(request.Headers["CSeq"]);
        var requestCSeqMethod = SipProtocol.ExtractCSeqMethod(request.Headers["CSeq"]) ?? method;
        var requestBranch = SipProtocol.ExtractViaBranch(request.Headers["Via"]);
        var transactionTimeout = ResolveTransactionTimeout(request);

        var provisionalResponses = new List<SipResponseEnvelope>();
        var provisionalSync = new object();
        var finalSync = new object();
        var provisionalReceived = false;
        var provisionalCapLogged = false;
        SipResponseEnvelope? firstFinalResponse = null;

        // §17.1.1.3: ACK headers for INVITE 3xx-6xx, built once and re-sent on retransmits
        IReadOnlyDictionary<string, string>? pendingAckHeaders = null;

        var provisionalReceivedSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalResponseTcs = new TaskCompletionSource<SipResponseEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(transactionTimeout);
        IDisposable? responseSubscription = null;

        responseSubscription = _transport.SubscribeResponses((remoteEndPoint, response) =>
        {
            if (!MatchesTransaction(
                    response,
                    callId,
                    requestCSeq,
                    requestCSeqMethod,
                    requestBranch))
            {
                return;
            }

            var normalizedResponse = NormalizeResponseForUac(response);
            var envelope = new SipResponseEnvelope(remoteEndPoint, normalizedResponse);
            if (SipProtocol.IsProvisional(normalizedResponse.StatusCode))
            {
                lock (finalSync)
                {
                    if (firstFinalResponse is not null)
                        return;
                }

                lock (provisionalSync)
                {
                    // #158 P1-8: retain at most MaxProvisionalResponses so an unbounded 1xx stream cannot grow
                    // the history without limit. Excess ones are still dispatched to the callback below.
                    if (provisionalResponses.Count < MaxProvisionalResponses)
                    {
                        provisionalResponses.Add(envelope);
                    }
                    else if (!provisionalCapLogged)
                    {
                        provisionalCapLogged = true;
                        _logger.LogDebug(
                            "SIP provisional-response history capped at {Cap} for {CallId}; excess 1xx not retained.",
                            MaxProvisionalResponses,
                            callId);
                    }

                    provisionalReceived = true;
                    provisionalReceivedSignal.TrySetResult(true);
                }

                try
                {
                    request.OnProvisionalResponse?.Invoke(envelope);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SIP provisional-response callback failed for {CallId}.", callId);
                }

                return;
            }

            SipResponseEnvelope? initialFinalResponse;
            lock (finalSync)
            {
                if (firstFinalResponse is null)
                {
                    firstFinalResponse = envelope;
                    initialFinalResponse = null;
                }
                else
                {
                    initialFinalResponse = firstFinalResponse;
                }
            }

            if (initialFinalResponse is null)
            {
                finalResponseTcs.TrySetResult(envelope);

                // RFC 3261 §17.1.1.3: INVITE client transaction MUST send ACK for 3xx-6xx responses.
                // The ACK uses the same Via branch as the INVITE (not a new branch — that is only for 2xx).
                if (isInvite && normalizedResponse.StatusCode >= 300)
                {
                    var ackHeaders = BuildInviteFailureAckHeaders(request, normalizedResponse);
                    lock (finalSync) { pendingAckHeaders = ackHeaders; }
                    FireAndForgetAck(request, ackHeaders, callId);
                }
                return;
            }

            // Retransmitted final response in Completed state.
            // §17.1.1.3: Re-send ACK for each retransmitted 3xx-6xx.
            IReadOnlyDictionary<string, string>? ackHeaders2;
            lock (finalSync) { ackHeaders2 = pendingAckHeaders; }
            if (ackHeaders2 is not null)
                FireAndForgetAck(request, ackHeaders2, callId);

            HandleRetransmittedFinalResponse(
                method,
                request,
                callId,
                initialFinalResponse.Value,
                envelope);
        });

        var retransmissionTask = RunRetransmissionLoopAsync(
            request,
            method,
            () =>
            {
                lock (provisionalSync)
                    return provisionalReceived;
            },
            provisionalReceivedSignal.Task,
            finalResponseTcs.Task,
            timeoutCts.Token);

        try
        {
            var finalResponse = await WaitForFinalResponseOrTransportFailureAsync(
                    finalResponseTcs.Task,
                    retransmissionTask,
                    timeoutCts.Token,
                    callId,
                    method)
                .ConfigureAwait(false);

            var sendAttempts = 0;
            try
            {
                sendAttempts = await retransmissionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal completion race after final response
            }

            SipResponseEnvelope[] provisionalSnapshot;
            lock (provisionalSync)
                provisionalSnapshot = provisionalResponses.ToArray();

            var completedRetention = ResolveCompletedStateRetention(
                request,
                method,
                finalResponse.Response.StatusCode);
            if (completedRetention > TimeSpan.Zero && responseSubscription is not null)
            {
                ArmDelayedSubscriptionDisposal(
                    responseSubscription,
                    completedRetention,
                    callId,
                    method);
                responseSubscription = null;
            }

            return new SipClientTransactionResult
            {
                FinalResponse = finalResponse,
                ProvisionalResponses = provisionalSnapshot,
                SendAttempts = sendAttempts
            };
        }
        finally
        {
            try
            {
                timeoutCts.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP transaction timeout token cancellation failed for {CallId}.", callId);
            }

            DisposeSubscriptionSafe(responseSubscription, callId, method);
        }
    }

    /// <summary>
    /// Re-sends request frames according to SIP client-transaction timer rules.
    /// </summary>
    private async Task<int> RunRetransmissionLoopAsync(
        SipClientTransactionRequest request,
        string method,
        Func<bool> hasProvisionalResponse,
        Task provisionalReceivedTask,
        Task finalResponseTask,
        CancellationToken ct)
    {
        var sendAttempts = 0;
        var interval = request.T1;
        var isUdp = request.Transport == SipTransportProtocol.Udp;
        var isInviteTransaction = string.Equals(method, "INVITE", StringComparison.Ordinal);

        await SendOnceAsync(request, ct).ConfigureAwait(false);
        sendAttempts++;

        if (!isUdp)
            return sendAttempts;

        while (!ct.IsCancellationRequested)
        {
            if (finalResponseTask.IsCompleted)
                break;

            if (hasProvisionalResponse())
            {
                // INVITE client transactions stop retransmissions after first provisional response.
                if (isInviteTransaction)
                    break;

                // Non-INVITE transactions switch to fixed T2 interval in Proceeding.
                interval = request.T2;
            }

            try
            {
                if (isInviteTransaction || hasProvisionalResponse())
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                else
                {
                    var waitOutcome = await WaitForDelayOrTransactionSignalAsync(
                            interval,
                            provisionalReceivedTask,
                            finalResponseTask,
                            ct)
                        .ConfigureAwait(false);
                    if (waitOutcome == RetransmissionWaitOutcome.FinalResponseReceived)
                        break;
                    if (waitOutcome == RetransmissionWaitOutcome.ProvisionalReceived)
                    {
                        interval = request.T2;
                        continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (finalResponseTask.IsCompleted)
                break;

            if (hasProvisionalResponse())
            {
                if (isInviteTransaction)
                    break;

                interval = request.T2;
            }

            await SendOnceAsync(request, ct).ConfigureAwait(false);
            sendAttempts++;
            interval = isInviteTransaction
                ? TimeSpan.FromMilliseconds(interval.TotalMilliseconds * 2)
                : TimeSpan.FromMilliseconds(
                    Math.Min(request.T2.TotalMilliseconds, interval.TotalMilliseconds * 2));
        }

        return sendAttempts;
    }

    /// <summary>
    /// Waits until one retransmission delay elapses or an earlier transaction signal arrives.
    /// </summary>
    private static async Task<RetransmissionWaitOutcome> WaitForDelayOrTransactionSignalAsync(
        TimeSpan delay,
        Task provisionalReceivedTask,
        Task finalResponseTask,
        CancellationToken ct)
    {
        var delayTask = Task.Delay(delay, ct);
        var completed = await Task.WhenAny(delayTask, provisionalReceivedTask, finalResponseTask)
            .ConfigureAwait(false);
        if (completed == finalResponseTask)
            return RetransmissionWaitOutcome.FinalResponseReceived;
        if (completed == provisionalReceivedTask)
            return RetransmissionWaitOutcome.ProvisionalReceived;

        await delayTask.ConfigureAwait(false);
        return RetransmissionWaitOutcome.DelayElapsed;
    }

    /// <summary>
    /// Handles retransmitted final responses observed after initial transaction completion.
    /// </summary>
    private void HandleRetransmittedFinalResponse(
        string method,
        SipClientTransactionRequest request,
        string callId,
        SipResponseEnvelope initialFinalResponse,
        SipResponseEnvelope retransmittedFinalResponse)
    {
        if (!string.Equals(method, "INVITE", StringComparison.Ordinal))
            return;
        if (request.Transport != SipTransportProtocol.Udp)
            return;
        if (initialFinalResponse.Response.StatusCode is < 300 or > 699)
            return;
        if (retransmittedFinalResponse.Response.StatusCode != initialFinalResponse.Response.StatusCode)
            return;
        if (request.OnInviteFailureFinalResponseRetransmission is null)
            return;

        try
        {
            request.OnInviteFailureFinalResponseRetransmission(retransmittedFinalResponse);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP final-response retransmission callback failed for {CallId}.", callId);
        }
    }

    /// <summary>
    /// Resolves retention window for post-final completed-state response absorption.
    /// </summary>
    private static TimeSpan ResolveCompletedStateRetention(
        SipClientTransactionRequest request,
        string method,
        int finalStatusCode)
    {
        if (request.Transport != SipTransportProtocol.Udp)
            return TimeSpan.Zero;

        if (string.Equals(method, "INVITE", StringComparison.Ordinal))
        {
            if (finalStatusCode is >= 300 and <= 699)
                return request.InviteFailureCompletedRetention ?? DefaultInviteFailureCompletedRetention;
            return TimeSpan.Zero;
        }

        return request.NonInviteCompletedRetention ?? DefaultNonInviteCompletedRetention;
    }

    /// <summary>
    /// Resolves the transaction timeout: an explicit <see cref="SipClientTransactionRequest.Timeout"/> is honoured
    /// as-is (even when it equals the default), otherwise the RFC 3261 64*T1 timeout is derived from
    /// <see cref="SipClientTransactionRequest.T1"/>. Internal for direct unit testing of the derivation.
    /// </summary>
    internal static TimeSpan ResolveTransactionTimeout(SipClientTransactionRequest request)
    {
        if (request.Timeout is { } explicitTimeout)
            return explicitTimeout;

        var derivedMilliseconds = request.T1.TotalMilliseconds * 64d;
        if (double.IsInfinity(derivedMilliseconds) || double.IsNaN(derivedMilliseconds) || derivedMilliseconds <= 0d)
            return DefaultTransactionTimeout;

        return TimeSpan.FromMilliseconds(derivedMilliseconds);
    }

    /// <summary>
    /// Schedules delayed response-subscription disposal for completed-state retention.
    /// </summary>
    private void ArmDelayedSubscriptionDisposal(
        IDisposable subscription,
        TimeSpan delay,
        string callId,
        string method)
    {
        if (delay <= TimeSpan.Zero)
        {
            DisposeSubscriptionSafe(subscription, callId, method);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "SIP client transaction delayed subscription disposal wait failed for {Method} on {CallId}.",
                    method,
                    callId);
            }
            finally
            {
                DisposeSubscriptionSafe(subscription, callId, method);
            }
        });
    }

    /// <summary>
    /// Disposes one response subscription with exception-safe logging.
    /// </summary>
    private void DisposeSubscriptionSafe(
        IDisposable? subscription,
        string callId,
        string method)
    {
        if (subscription is null)
            return;

        try
        {
            subscription.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "SIP client transaction response subscription disposal failed for {Method} on {CallId}.",
                method,
                callId);
        }
    }

    /// <summary>
    /// Sends one outbound request frame through transport runtime.
    /// </summary>
    private async Task SendOnceAsync(
        SipClientTransactionRequest request,
        CancellationToken ct)
    {
        await _transport.SendRequestAsync(
                request.Method,
                request.RequestUri,
                request.Headers,
                request.Body,
                request.RemoteEndPoint,
                request.Transport,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for final response or fails fast on transport send errors.
    /// </summary>
    private async Task<SipResponseEnvelope> WaitForFinalResponseOrTransportFailureAsync(
        Task<SipResponseEnvelope> finalResponseTask,
        Task retransmissionTask,
        CancellationToken timeoutToken,
        string callId,
        string method)
    {
        Task completedTask;
        try
        {
            completedTask = await Task.WhenAny(finalResponseTask, retransmissionTask)
                .WaitAsync(timeoutToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(
                ex,
                "SIP client transaction timed out waiting for final {Method} response on {CallId}.",
                method,
                callId);
            throw new TimeoutException($"Timed out waiting for final SIP response to {method}.");
        }

        if (completedTask == finalResponseTask)
            return await finalResponseTask.ConfigureAwait(false);

        if (retransmissionTask.IsFaulted)
        {
            var transportException = retransmissionTask.Exception?.GetBaseException()
                ?? new InvalidOperationException("Unknown SIP transaction transport failure.");
            _logger.LogDebug(
                transportException,
                "SIP client transaction send failed for {Method} on {CallId}.",
                method,
                callId);
            throw new SipTransactionTransportException(
                $"SIP transaction send failed for {method}.",
                transportException);
        }

        return await WaitForFinalResponseAsync(
                finalResponseTask,
                timeoutToken,
                callId,
                method)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the final transaction response with timeout conversion.
    /// </summary>
    private async Task<SipResponseEnvelope> WaitForFinalResponseAsync(
        Task<SipResponseEnvelope> finalResponseTask,
        CancellationToken timeoutToken,
        string callId,
        string method)
    {
        try
        {
            return await finalResponseTask.WaitAsync(timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(
                ex,
                "SIP client transaction timed out waiting for final {Method} response on {CallId}.",
                method,
                callId);
            throw new TimeoutException($"Timed out waiting for final SIP response to {method}.");
        }
    }

    /// <summary>
    /// Returns true when an inbound response matches transaction identity.
    /// </summary>
    internal static bool MatchesTransaction(
        SipResponse response,
        string callId,
        int requestCSeq,
        string requestCSeqMethod,
        string? requestBranch)
    {
        if (CountViaValues(response) != 1)
            return false;

        if (!string.Equals(response.Header("Call-ID"), callId, StringComparison.Ordinal))
            return false;

        var responseCSeq = response.Header("CSeq");
        if (SipProtocol.ExtractCSeqNumber(responseCSeq) != requestCSeq)
            return false;

        if (!string.Equals(
                SipProtocol.ExtractCSeqMethod(responseCSeq),
                requestCSeqMethod,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestBranch))
            return true;

        // RFC 3261 §17.1.3: a response matches the client transaction only when its top-Via branch is present
        // AND equal to the request's branch. A response without a branch is not a match (previously accepted).
        var responseBranch = SipProtocol.ExtractViaBranch(response.Header("Via"));
        return !string.IsNullOrWhiteSpace(responseBranch)
            && string.Equals(responseBranch, requestBranch, StringComparison.Ordinal);
    }

    /// <summary>
    /// Counts all Via header field values in one SIP response.
    /// </summary>
    private static int CountViaValues(SipResponse response)
    {
        var count = 0;
        foreach (var viaRow in response.HeaderValues("Via"))
            count += SipProtocol.CountViaHeaderValues(viaRow);
        return count;
    }

    /// <summary>
    /// Normalizes response status code handling according to RFC3261 section 8.1.3.2.
    /// </summary>
    private static SipResponse NormalizeResponseForUac(SipResponse response)
    {
        var normalizedStatusCode = SipProtocol.NormalizeUacResponseStatusCode(response.StatusCode);
        if (normalizedStatusCode == response.StatusCode)
            return response;

        return new SipResponse(
            normalizedStatusCode,
            response.ReasonPhrase,
            response.Headers,
            response.Body);
    }

    /// <summary>
    /// Validates transaction request contract before network I/O starts.
    /// </summary>
    private static void ValidateRequest(SipClientTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Method))
            throw new ArgumentException("Method is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RequestUri))
            throw new ArgumentException("RequestUri is required.", nameof(request));
        if (request.Headers is null)
            throw new ArgumentException("Headers are required.", nameof(request));
        if (!request.Headers.ContainsKey("Call-ID"))
            throw new ArgumentException("Headers must contain Call-ID.", nameof(request));
        if (!request.Headers.ContainsKey("CSeq"))
            throw new ArgumentException("Headers must contain CSeq.", nameof(request));
        if (!request.Headers.ContainsKey("From"))
            throw new ArgumentException("Headers must contain From.", nameof(request));
        if (!request.Headers.ContainsKey("To"))
            throw new ArgumentException("Headers must contain To.", nameof(request));
        if (!request.Headers.ContainsKey("Max-Forwards"))
            throw new ArgumentException("Headers must contain Max-Forwards.", nameof(request));
        if (!request.Headers.ContainsKey("Via"))
            throw new ArgumentException("Headers must contain Via.", nameof(request));
        if (!int.TryParse(request.Headers["Max-Forwards"], out var maxForwards) || maxForwards < 0)
            throw new ArgumentException("Max-Forwards must be a non-negative integer.", nameof(request));

        var normalizedMethod = request.Method.Trim().ToUpperInvariant();
        var cseqHeader = request.Headers["CSeq"];
        var cseqNumber = SipProtocol.ExtractCSeqNumber(cseqHeader);
        if (cseqNumber <= 0)
            throw new ArgumentException("CSeq number must be greater than 0.", nameof(request));
        if (!string.Equals(
                SipProtocol.ExtractCSeqMethod(cseqHeader),
                normalizedMethod,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("CSeq method must match request method.", nameof(request));
        }

        var branch = SipProtocol.ExtractViaBranch(request.Headers["Via"]);
        if (string.IsNullOrWhiteSpace(branch) || !SipProtocol.HasMagicCookie(branch))
            throw new ArgumentException("Via branch must be present and start with RFC3261 magic cookie.", nameof(request));

        if (request.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be positive.");
        if (request.T1 <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "T1 must be positive.");
        if (request.T2 < request.T1)
            throw new ArgumentOutOfRangeException(nameof(request), "T2 must be >= T1.");
        if (request.InviteFailureCompletedRetention is { } timerD && timerD <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "InviteFailureCompletedRetention must be positive.");
        if (request.NonInviteCompletedRetention is { } timerK && timerK <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "NonInviteCompletedRetention must be positive.");
    }

    /// <summary>
    /// Builds ACK request headers for INVITE 3xx-6xx responses per RFC 3261 §17.1.1.3.
    /// The ACK uses the same Via branch as the INVITE, From/Call-ID/CSeq unchanged,
    /// and To from the received response (which includes the UAS To-tag).
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildInviteFailureAckHeaders(
        SipClientTransactionRequest request,
        SipResponse failureResponse)
    {
        var ackCSeq = $"{SipProtocol.ExtractCSeqNumber(request.Headers["CSeq"])} ACK";
        var toFromResponse = failureResponse.Header("To") ?? request.Headers["To"];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"]            = request.Headers["Via"],   // same branch — §17.1.1.3
            ["From"]           = request.Headers["From"],
            ["To"]             = toFromResponse,
            ["Call-ID"]        = request.Headers["Call-ID"],
            ["CSeq"]           = ackCSeq,
            ["Max-Forwards"]   = "70",
            ["Content-Length"] = "0",
        };

        if (request.Headers.TryGetValue("Route", out var route))
            headers["Route"] = route;

        return headers;
    }

    /// <summary>
    /// Sends ACK as fire-and-forget; logs transport failures without surfacing them.
    /// </summary>
    private void FireAndForgetAck(
        SipClientTransactionRequest request,
        IReadOnlyDictionary<string, string> ackHeaders,
        string callId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _transport.SendRequestAsync(
                        "ACK",
                        request.RequestUri,
                        ackHeaders,
                        body: null,
                        request.RemoteEndPoint,
                        request.Transport,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP INVITE failure ACK send failed for {CallId}.", callId);
            }
        });
    }

    /// <summary>
    /// Result of waiting for retransmission timing gates.
    /// </summary>
    private enum RetransmissionWaitOutcome
    {
        DelayElapsed,
        ProvisionalReceived,
        FinalResponseReceived
    }
}
