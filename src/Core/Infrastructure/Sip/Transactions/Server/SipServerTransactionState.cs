using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;

/// <summary>
/// Mutable runtime state for one SIP server transaction.
/// Tracks remote endpoint/transport, retransmission snapshots, and protocol timers.
/// </summary>
internal sealed class SipServerTransactionState : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly object _timerSync = new();
    private SipServerTransactionResponseSnapshot? _lastResponse;
    private IDisposable? _inviteFailureRetransmitTimer;
    private IDisposable? _inviteSuccessRetransmitTimer;
    private IDisposable? _cleanupTimer;

    /// <summary>
    /// Creates state container for one server transaction key.
    /// </summary>
    public SipServerTransactionState(
        SipServerTransactionKey key,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        string method,
        ILogger logger,
        int? inboundConnectionId = null)
    {
        Key = key;
        RemoteEndPoint = remoteEndPoint;
        Transport = transport;
        InboundConnectionId = inboundConnectionId;
        Method = method;
        _logger = logger;
    }

    /// <summary>
    /// Stable transaction key.
    /// </summary>
    public SipServerTransactionKey Key { get; }

    /// <summary>
    /// Current remote endpoint associated with this transaction.
    /// </summary>
    public IPEndPoint RemoteEndPoint { get; private set; }

    /// <summary>
    /// Current transport associated with this transaction.
    /// </summary>
    public SipTransportProtocol Transport { get; private set; }

    /// <summary>
    /// Identifier of the accepted inbound connection this transaction's request arrived on, or <c>null</c>
    /// for connectionless (UDP) receipt. Responses are routed back over this exact connection (#158 P1-2).
    /// </summary>
    public int? InboundConnectionId { get; private set; }

    /// <summary>
    /// Request method this transaction belongs to.
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// Returns true when this state belongs to an INVITE server transaction.
    /// </summary>
    public bool IsInvite => string.Equals(Method, "INVITE", StringComparison.Ordinal);

    /// <summary>
    /// Flag set when first request for this transaction has been observed.
    /// </summary>
    public int HasSeenRequest;

    /// <summary>
    /// Flag set when INVITE error retransmit loop has started.
    /// </summary>
    public int InviteRetransmitStarted;

    /// <summary>
    /// Flag set when INVITE success retransmit loop has started.
    /// </summary>
    public int InviteSuccessRetransmitStarted;

    /// <summary>
    /// Flag set when cleanup timer has started.
    /// </summary>
    public int CleanupStarted;

    /// <summary>
    /// Cancellation token for ACK-driven completion of INVITE error retransmissions.
    /// </summary>
    public CancellationTokenSource AckCancellation { get; } = new();

    /// <summary>
    /// Cancellation token for lifecycle cleanup scheduling.
    /// </summary>
    public CancellationTokenSource CleanupCancellation { get; } = new();

    /// <summary>
    /// Replaces current INVITE failure retransmit timer handle.
    /// Any previously registered handle is disposed first.
    /// </summary>
    public void ReplaceInviteFailureRetransmitTimer(IDisposable timerHandle)
    {
        ArgumentNullException.ThrowIfNull(timerHandle);
        IDisposable? previous;
        lock (_timerSync)
        {
            previous = _inviteFailureRetransmitTimer;
            _inviteFailureRetransmitTimer = timerHandle;
        }

        DisposeTimerHandleSafe(previous, "invite failure retransmit");
    }

    /// <summary>
    /// Replaces current INVITE success retransmit timer handle.
    /// Any previously registered handle is disposed first.
    /// </summary>
    public void ReplaceInviteSuccessRetransmitTimer(IDisposable timerHandle)
    {
        ArgumentNullException.ThrowIfNull(timerHandle);
        IDisposable? previous;
        lock (_timerSync)
        {
            previous = _inviteSuccessRetransmitTimer;
            _inviteSuccessRetransmitTimer = timerHandle;
        }

        DisposeTimerHandleSafe(previous, "invite success retransmit");
    }

    /// <summary>
    /// Replaces current cleanup timer handle.
    /// Any previously registered handle is disposed first.
    /// </summary>
    public void ReplaceCleanupTimer(IDisposable timerHandle)
    {
        ArgumentNullException.ThrowIfNull(timerHandle);
        IDisposable? previous;
        lock (_timerSync)
        {
            previous = _cleanupTimer;
            _cleanupTimer = timerHandle;
        }

        DisposeTimerHandleSafe(previous, "cleanup");
    }

    /// <summary>
    /// Cancels and clears both INVITE retransmission timers.
    /// </summary>
    public void CancelInviteRetransmissionTimers()
    {
        IDisposable? failureHandle;
        IDisposable? successHandle;
        lock (_timerSync)
        {
            failureHandle = _inviteFailureRetransmitTimer;
            _inviteFailureRetransmitTimer = null;
            successHandle = _inviteSuccessRetransmitTimer;
            _inviteSuccessRetransmitTimer = null;
        }

        DisposeTimerHandleSafe(failureHandle, "invite failure retransmit");
        DisposeTimerHandleSafe(successHandle, "invite success retransmit");
    }

    /// <summary>
    /// Cancels and clears cleanup timer.
    /// </summary>
    public void CancelCleanupTimer()
    {
        IDisposable? cleanupHandle;
        lock (_timerSync)
        {
            cleanupHandle = _cleanupTimer;
            _cleanupTimer = null;
        }

        DisposeTimerHandleSafe(cleanupHandle, "cleanup");
    }

    /// <summary>
    /// Adopts the accepted inbound connection for this transaction: the real receive transport, its packet
    /// source and (for connection-oriented transports) its connection id, applied atomically. Called on
    /// registration and on every request retransmission so a retransmit arriving on a fresh connection
    /// re-points subsequent responses at that connection (#158 P1-2).
    /// </summary>
    public void AdoptInboundConnection(IPEndPoint remoteEndPoint, SipTransportProtocol transport, int? inboundConnectionId)
    {
        lock (_sync)
        {
            RemoteEndPoint = remoteEndPoint;
            Transport = transport;
            InboundConnectionId = inboundConnectionId;
        }
    }

    /// <summary>
    /// Updates only the remote endpoint for subsequent response retransmissions (e.g. a UDP §18.2.2
    /// rport-resolved destination), leaving the authoritative transport and inbound connection id — set from
    /// the accepted connection at registration — unchanged so a Via-derived caller transport can never
    /// override the real one (#158 P1-2).
    /// </summary>
    public void UpdateRemoteEndPoint(IPEndPoint remoteEndPoint)
    {
        lock (_sync)
            RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>
    /// Stores latest sent response snapshot for retransmission.
    /// </summary>
    public void SetLastResponse(SipServerTransactionResponseSnapshot response)
    {
        lock (_sync)
            _lastResponse = response;
    }

    /// <summary>
    /// Returns latest sent response snapshot when available.
    /// </summary>
    public SipServerTransactionResponseSnapshot? GetLastResponse()
    {
        lock (_sync)
            return _lastResponse;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancelInviteRetransmissionTimers();
        CancelCleanupTimer();

        try
        {
            AckCancellation.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP server transaction ACK cancellation failed.");
        }

        try
        {
            CleanupCancellation.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP server transaction cleanup cancellation failed.");
        }

        AckCancellation.Dispose();
        CleanupCancellation.Dispose();
    }

    /// <summary>
    /// Disposes one timer handle while logging disposal failures.
    /// </summary>
    private void DisposeTimerHandleSafe(IDisposable? handle, string timerName)
    {
        if (handle is null)
            return;

        try
        {
            handle.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP server transaction {TimerName} timer handle disposal failed.", timerName);
        }
    }
}
