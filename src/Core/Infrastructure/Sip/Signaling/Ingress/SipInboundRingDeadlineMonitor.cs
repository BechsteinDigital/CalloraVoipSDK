using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Timing;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// #158 P1-5 (ring deadline): bounds how long a freshly-created inbound dialog session may sit in
/// <see cref="SipDialogState.Ringing"/> without the consumer answering or rejecting it. A UAS creates dialog
/// state (and raises IncomingInvite) for every served-user INVITE before any line/trunk takes ownership; a
/// session the application never answers would otherwise stay in <c>Ringing</c> — and pinned in the signaling
/// service's session map — indefinitely. On expiry the session is rejected with 480 Temporarily Unavailable
/// (RFC 3261 §21.4.18), which drives it to <c>Terminated</c> and lets the normal lifecycle cleanup remove it.
/// </summary>
internal sealed class SipInboundRingDeadlineMonitor : IDisposable
{
    /// <summary>
    /// Default alerting deadline. UAS alerting has no single mandated timeout in RFC 3261; three minutes is a
    /// conventional upper bound (aligned with the common 180 s network no-answer timer) that still frees state
    /// long before it can accumulate.
    /// </summary>
    private static readonly TimeSpan DefaultRingDeadline = TimeSpan.FromSeconds(180);

    private readonly TimeSpan _deadline;
    private readonly IScheduledActionScheduler _scheduler;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IDisposable> _timers = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>
    /// Creates a ring-deadline monitor.
    /// </summary>
    /// <param name="logger">Logger for expiry and failure diagnostics.</param>
    /// <param name="deadline">Ring deadline; non-positive or null falls back to the 180 s default.</param>
    /// <param name="scheduler">Optional scheduler override (primarily for deterministic tests); a real
    /// <see cref="ScheduledActionScheduler"/> is created when null.</param>
    public SipInboundRingDeadlineMonitor(
        ILogger logger,
        TimeSpan? deadline = null,
        IScheduledActionScheduler? scheduler = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deadline = deadline is { } value && value > TimeSpan.Zero ? value : DefaultRingDeadline;
        _scheduler = scheduler ?? new ScheduledActionScheduler(logger);
    }

    /// <summary>
    /// Starts the ring deadline for one freshly-created inbound session. Call before raising IncomingInvite so
    /// a consumer that answers or rejects synchronously still cancels the timer via <see cref="Cancel"/>.
    /// </summary>
    public void Track(SipCallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Volatile.Read(ref _disposed) != 0) return;

        var callId = session.CallId;
        var handle = _scheduler.Schedule(_deadline, () => OnDeadlineElapsed(callId, session));
        // A fresh Call-ID should not already have a timer; if it somehow does, the newest wins and the stale
        // handle is disposed so it cannot fire against a superseded session.
        _timers.TryGetValue(callId, out var superseded);
        _timers[callId] = handle;
        superseded?.Dispose();

        // Race close: if the consumer already drove the session out of Ringing (synchronous answer/reject) or
        // the monitor was disposed between the guard above and the store, cancel the just-stored timer now.
        if (session.State != SipDialogState.Ringing || Volatile.Read(ref _disposed) != 0)
            Cancel(callId);
    }

    /// <summary>
    /// Cancels the ring deadline for one session (it was answered, rejected, or otherwise terminated). Safe to
    /// call for sessions that were never tracked (e.g. outbound dialogs) — it is a no-op then.
    /// </summary>
    public void Cancel(string callId)
    {
        if (_timers.TryRemove(callId, out var handle))
            handle.Dispose();
    }

    private void OnDeadlineElapsed(string callId, SipCallSession session)
    {
        _timers.TryRemove(callId, out _);
        // Only a still-ringing session is stale; anything else was answered or already terminated.
        if (session.State != SipDialogState.Ringing)
            return;
        _ = RejectExpiredAsync(callId, session);
    }

    private async Task RejectExpiredAsync(string callId, SipCallSession session)
    {
        try
        {
            _logger.LogWarning(
                "Inbound session {CallId} exceeded the ring deadline ({Deadline}) without an answer; rejecting 480 Temporarily Unavailable.",
                callId,
                _deadline);
            await session.RejectAsync(480, "Temporarily Unavailable").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: a concurrent answer/reject may have moved the session out of Ringing (RejectAsync
            // then throws InvalidOperationException) or the transport may be gone. Either way the session is no
            // longer stale; log at debug and let normal lifecycle cleanup proceed.
            _logger.LogDebug(
                ex,
                "Ring-deadline 480 for inbound session {CallId} did not apply (already answered or terminated?).",
                callId);
        }
    }

    /// <summary>
    /// Cancels every outstanding ring deadline and disposes the underlying scheduler.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var handle in _timers.Values)
            handle.Dispose();
        _timers.Clear();
        _scheduler.Dispose();
    }
}
