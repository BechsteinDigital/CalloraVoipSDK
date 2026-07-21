using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;

/// <summary>
/// Default SIP server transaction engine supporting INVITE and non-INVITE server transactions.
/// </summary>
internal sealed class SipServerTransactionEngine : ISipServerTransactionEngine
{
    private static readonly TimeSpan T1 = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan T2 = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan T4 = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TimerH = TimeSpan.FromSeconds(32);
    private static readonly TimeSpan TimerJ = TimeSpan.FromSeconds(32);
    private static readonly TimeSpan TimerL = TimeSpan.FromSeconds(32);

    private readonly ISipTransportRuntime _transport;
    private readonly ILogger _logger;
    private readonly IScheduledActionScheduler _timerScheduler;
    private readonly ConcurrentDictionary<SipServerTransactionKey, SipServerTransactionState> _transactions = new();
    private volatile Action<SipServerTransactionKey, Exception>? _transportErrorHandler;
    private int _disposed;

    /// <summary>
    /// Creates a server transaction engine bound to one transport runtime.
    /// </summary>
    public SipServerTransactionEngine(
        ISipTransportRuntime transport,
        ILogger logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timerScheduler = new ScheduledActionScheduler(_logger);
    }

    /// <inheritdoc />
    public SipServerTransactionRegistration RegisterInboundRequest(
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        SipRequest request)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return default;

        var method = request.Method.ToUpperInvariant();
        if (method == "ACK")
        {
            AcknowledgeInviteTransactions(request);
            return new SipServerTransactionRegistration { IsAck = true };
        }

        if (!SipServerTransactionKey.TryFromRequest(request, out var key))
            return default;

        var state = _transactions.GetOrAdd(key, _ => new SipServerTransactionState(
            key,
            remoteEndPoint,
            transport,
            method,
            _logger));
        state.UpdateRemote(remoteEndPoint, transport);

        var isRetransmission = Interlocked.CompareExchange(ref state.HasSeenRequest, 1, 0) == 1;
        if (isRetransmission)
        {
            _ = TryResendLastResponseAsync(state);
        }

        return new SipServerTransactionRegistration
        {
            IsRetransmission = isRetransmission
        };
    }

    /// <inheritdoc />
    public async Task SendResponseAsync(
        SipRequest request,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipServerTransactionEngine));

        // RFC 3261 §18.2.1 / RFC 3581 §4 (CF-040): reflect received=/rport= into the outgoing response's top Via
        // against the actual packet source FIRST — critically, this fills a bare ";rport" (what a UAC sends to
        // request rport processing) with the real source port. The §18.2.2 destination below is then derived from
        // the *reflected* Via, so the datagram goes to the actual source port, not the sent-by port. (Resolving
        // first would read the still-bare rport, get no value, and route to the sent-by port — right header,
        // wrong datagram.) Idempotent: a Via already reflected upstream is unchanged, and a response whose header
        // builder did not reflect (e.g. an ingress OPTIONS 200) is reflected here for every transaction response.
        if (headers.TryGetValue("Via", out var outgoingVia))
        {
            var reflectedVia = SipProtocol.ReflectViaParameters(outgoingVia, remoteEndPoint);
            if (!string.Equals(reflectedVia, outgoingVia, StringComparison.Ordinal))
            {
                headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                {
                    ["Via"] = reflectedVia
                };
            }
        }

        // RFC 3261 §18.2.2: for UDP, route the response via the (now reflected) Via header parameters
        // (received/rport), not simply the raw packet-source endpoint.
        if (transport == SipTransportProtocol.Udp
            && headers.TryGetValue("Via", out var responseVia))
        {
            remoteEndPoint = SipProtocol.ResolveUdpResponseDestination(responseVia, remoteEndPoint);
        }

        if (!SipServerTransactionKey.TryFromRequest(request, out var key))
        {
            await _transport.SendResponseAsync(
                    statusCode,
                    reasonPhrase,
                    headers,
                    body,
                    remoteEndPoint,
                    transport,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var method = request.Method.ToUpperInvariant();
        var state = _transactions.GetOrAdd(key, _ => new SipServerTransactionState(
            key,
            remoteEndPoint,
            transport,
            method,
            _logger));
        state.UpdateRemote(remoteEndPoint, transport);

        if (ShouldDiscardAdditionalFinalResponse(
                state,
                statusCode))
        {
            return;
        }

        var snapshot = new SipServerTransactionResponseSnapshot(
            statusCode,
            reasonPhrase,
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            body);
        state.SetLastResponse(snapshot);

        await SendSnapshotAsync(state, snapshot, ct).ConfigureAwait(false);
        StartOrRefreshTimers(state, snapshot);
    }

    /// <summary>
    /// Returns true when an additional final response should be discarded in completed-state semantics.
    /// </summary>
    private bool ShouldDiscardAdditionalFinalResponse(
        SipServerTransactionState state,
        int nextStatusCode)
    {
        if (nextStatusCode < 200)
            return false;

        var previous = state.GetLastResponse();
        if (previous is null || previous.StatusCode < 200)
            return false;

        _logger.LogDebug(
            "Discarding additional final SIP response {PreviousStatus}->{NextStatus} for {CallId}.",
            previous.StatusCode,
            nextStatusCode,
            state.Key.CallId);
        return true;
    }

    /// <summary>
    /// Re-sends last response for request retransmissions when available.
    /// On transport failure, terminates the transaction per RFC 3261 §17.2.4.
    /// </summary>
    private async Task TryResendLastResponseAsync(SipServerTransactionState state)
    {
        var snapshot = state.GetLastResponse();
        if (snapshot is null)
            return;

        try
        {
            await SendSnapshotAsync(state, snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RFC 3261 §17.2.4: fatal transport error — terminate the server transaction.
            _logger.LogWarning(
                ex,
                "SIP server transaction retransmit transport error for {CallId} — terminating transaction.",
                state.Key.CallId);

            if (_transactions.TryRemove(state.Key, out var removed))
                removed.Dispose();

            try
            {
                _transportErrorHandler?.Invoke(state.Key, ex);
            }
            catch (Exception cbEx)
            {
                _logger.LogDebug(cbEx, "SIP server transaction transport-error handler threw for {CallId}.", state.Key.CallId);
            }
        }
    }

    /// <summary>
    /// Sends one stored response snapshot.
    /// </summary>
    private Task SendSnapshotAsync(
        SipServerTransactionState state,
        SipServerTransactionResponseSnapshot snapshot,
        CancellationToken ct) =>
        _transport.SendResponseAsync(
            snapshot.StatusCode,
            snapshot.ReasonPhrase,
            snapshot.Headers,
            snapshot.Body,
            state.RemoteEndPoint,
            state.Transport,
            ct);

    /// <summary>
    /// Starts transaction timers based on response class and method type.
    /// </summary>
    private void StartOrRefreshTimers(SipServerTransactionState state, SipServerTransactionResponseSnapshot response)
    {
        if (response.StatusCode < 200)
            return;

        if (state.IsInvite)
        {
            if (response.StatusCode is >= 200 and < 300 && state.Transport == SipTransportProtocol.Udp)
                ArmInviteSuccessRetransmit(state);
            if (response.StatusCode is >= 300 and <= 699 && state.Transport == SipTransportProtocol.Udp)
                ArmInviteFailureRetransmit(state);
            ArmCleanupTimer(state, TimerL);
            return;
        }

        ArmCleanupTimer(state, state.Transport == SipTransportProtocol.Udp ? TimerJ : TimeSpan.Zero);
    }

    /// <summary>
    /// Arms scheduler-driven retransmits for final non-2xx INVITE responses.
    /// </summary>
    private void ArmInviteFailureRetransmit(SipServerTransactionState state)
    {
        if (Interlocked.Exchange(ref state.InviteRetransmitStarted, 1) != 0)
            return;

        var startedAt = DateTimeOffset.UtcNow;
        ScheduleInviteFailureRetransmit(state, T1, startedAt);
    }

    /// <summary>
    /// Arms scheduler-driven retransmits for successful 2xx INVITE responses over UDP.
    /// </summary>
    private void ArmInviteSuccessRetransmit(SipServerTransactionState state)
    {
        if (Interlocked.Exchange(ref state.InviteSuccessRetransmitStarted, 1) != 0)
            return;

        var startedAt = DateTimeOffset.UtcNow;
        ScheduleInviteSuccessRetransmit(state, T1, startedAt);
    }

    /// <summary>
    /// Schedules one INVITE non-2xx retransmit callback.
    /// </summary>
    private void ScheduleInviteFailureRetransmit(
        SipServerTransactionState state,
        TimeSpan interval,
        DateTimeOffset startedAt)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var timerHandle = _timerScheduler.Schedule(
            interval,
            () => OnInviteFailureRetransmitDue(state, interval, startedAt));
        state.ReplaceInviteFailureRetransmitTimer(timerHandle);
    }

    /// <summary>
    /// Runs one INVITE non-2xx retransmit step and reschedules next interval when needed.
    /// </summary>
    private void OnInviteFailureRetransmitDue(
        SipServerTransactionState state,
        TimeSpan interval,
        DateTimeOffset startedAt)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (state.AckCancellation.IsCancellationRequested)
            return;
        if (DateTimeOffset.UtcNow - startedAt >= TimerH)
            return;

        var snapshot = state.GetLastResponse();
        if (snapshot is null || snapshot.StatusCode < 300 || snapshot.StatusCode > 699)
            return;

        _ = TrySendRetransmitSnapshotAsync(state, snapshot, "INVITE failure");

        var nextInterval = TimeSpan.FromMilliseconds(
            Math.Min(T2.TotalMilliseconds, interval.TotalMilliseconds * 2));
        ScheduleInviteFailureRetransmit(state, nextInterval, startedAt);
    }

    /// <summary>
    /// Schedules one INVITE 2xx retransmit callback.
    /// </summary>
    private void ScheduleInviteSuccessRetransmit(
        SipServerTransactionState state,
        TimeSpan interval,
        DateTimeOffset startedAt)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var timerHandle = _timerScheduler.Schedule(
            interval,
            () => OnInviteSuccessRetransmitDue(state, interval, startedAt));
        state.ReplaceInviteSuccessRetransmitTimer(timerHandle);
    }

    /// <summary>
    /// Runs one INVITE 2xx retransmit step and reschedules next interval when needed.
    /// </summary>
    private void OnInviteSuccessRetransmitDue(
        SipServerTransactionState state,
        TimeSpan interval,
        DateTimeOffset startedAt)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (state.AckCancellation.IsCancellationRequested)
            return;
        if (DateTimeOffset.UtcNow - startedAt >= TimerH)
            return;

        var snapshot = state.GetLastResponse();
        if (snapshot is null || snapshot.StatusCode < 200 || snapshot.StatusCode >= 300)
            return;

        _ = TrySendRetransmitSnapshotAsync(state, snapshot, "INVITE success");

        var nextInterval = TimeSpan.FromMilliseconds(
            Math.Min(T2.TotalMilliseconds, interval.TotalMilliseconds * 2));
        ScheduleInviteSuccessRetransmit(state, nextInterval, startedAt);
    }

    /// <summary>
    /// Sends one retransmit snapshot. On transport failure, terminates the transaction and
    /// notifies the registered handler per RFC 3261 §17.2.4.
    /// </summary>
    private async Task TrySendRetransmitSnapshotAsync(
        SipServerTransactionState state,
        SipServerTransactionResponseSnapshot snapshot,
        string retransmitKind)
    {
        try
        {
            await SendSnapshotAsync(state, snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RFC 3261 §17.2.4: fatal transport error — terminate the server transaction.
            _logger.LogWarning(
                ex,
                "SIP {RetransmitKind} retransmit transport error for {CallId} — terminating transaction.",
                retransmitKind,
                state.Key.CallId);

            if (_transactions.TryRemove(state.Key, out var removed))
                removed.Dispose();

            try
            {
                _transportErrorHandler?.Invoke(state.Key, ex);
            }
            catch (Exception cbEx)
            {
                _logger.LogDebug(cbEx, "SIP server transaction transport-error handler threw for {CallId}.", state.Key.CallId);
            }
        }
    }

    /// <inheritdoc />
    public void RegisterTransportErrorHandler(Action<SipServerTransactionKey, Exception> handler)
    {
        _transportErrorHandler = handler;
    }

    /// <summary>
    /// Acknowledges INVITE server transactions for inbound ACK matching.
    /// Handles ACK for 2xx where ACK branch differs from INVITE branch.
    /// </summary>
    private void AcknowledgeInviteTransactions(SipRequest ackRequest)
    {
        var callId = ackRequest.Header("Call-ID");
        var cseq = SipProtocol.ExtractCSeqNumber(ackRequest.Header("CSeq"));
        if (string.IsNullOrWhiteSpace(callId) || cseq <= 0)
            return;

        var ackFromTag = SipProtocol.ExtractTag(ackRequest.Header("From"));
        var ackToTag = SipProtocol.ExtractTag(ackRequest.Header("To"));

        foreach (var state in _transactions.Values)
        {
            if (!state.IsInvite)
                continue;
            if (!string.Equals(state.Key.CallId, callId, StringComparison.Ordinal))
                continue;
            if (state.Key.CSeqNumber != cseq)
                continue;

            var snapshot = state.GetLastResponse();
            if (!IsAckDialogMatch(snapshot, ackFromTag, ackToTag))
                continue;

            try
            {
                state.AckCancellation.Cancel();
                state.CancelInviteRetransmissionTimers();
                RearmCleanupTimerFromAck(state);
            }
            catch (ObjectDisposedException)
            {
                // transaction already disposing; safe to ignore
            }
        }
    }

    /// <summary>
    /// Returns true when ACK tags match the dialog identity from the stored INVITE response.
    /// </summary>
    private static bool IsAckDialogMatch(
        SipServerTransactionResponseSnapshot? snapshot,
        string? ackFromTag,
        string? ackToTag)
    {
        if (snapshot is null)
            return true;

        var responseFrom = TryGetHeader(snapshot.Headers, "From");
        var responseTo = TryGetHeader(snapshot.Headers, "To");
        var responseFromTag = SipProtocol.ExtractTag(responseFrom);
        var responseToTag = SipProtocol.ExtractTag(responseTo);

        if (!string.IsNullOrWhiteSpace(responseFromTag)
            && !string.IsNullOrWhiteSpace(ackFromTag)
            && !string.Equals(responseFromTag, ackFromTag, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(responseToTag)
            && !string.IsNullOrWhiteSpace(ackToTag)
            && !string.Equals(responseToTag, ackToTag, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tries to read a case-insensitive header value from one response snapshot.
    /// </summary>
    private static string? TryGetHeader(
        IReadOnlyDictionary<string, string> headers,
        string headerName)
    {
        if (headers.TryGetValue(headerName, out var value))
            return value;

        foreach (var pair in headers)
        {
            if (pair.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    /// <summary>
    /// Arms transaction cleanup timer using scheduler.
    /// </summary>
    private void ArmCleanupTimer(SipServerTransactionState state, TimeSpan delay)
    {
        if (Interlocked.Exchange(ref state.CleanupStarted, 1) != 0)
            return;

        var timerHandle = _timerScheduler.Schedule(delay, () => OnCleanupDue(state));
        state.ReplaceCleanupTimer(timerHandle);
    }

    /// <summary>
    /// Rearms cleanup timer when ACK confirms INVITE transaction completion.
    /// </summary>
    private void RearmCleanupTimerFromAck(SipServerTransactionState state)
    {
        var cleanupDelay = state.Transport == SipTransportProtocol.Udp
            ? T4
            : TimeSpan.Zero;
        var timerHandle = _timerScheduler.Schedule(cleanupDelay, () => OnCleanupDue(state));
        state.ReplaceCleanupTimer(timerHandle);
    }

    /// <summary>
    /// Executes transaction cleanup when timer expires.
    /// </summary>
    private void OnCleanupDue(SipServerTransactionState state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (_transactions.TryRemove(state.Key, out var removed))
            removed.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var state in _transactions.Values)
            state.Dispose();
        _transactions.Clear();
        _timerScheduler.Dispose();
    }
}
