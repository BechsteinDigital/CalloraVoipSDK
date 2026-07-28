using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Infrastructure implementation of <see cref="IReferSubscription"/> for the implicit subscription created by an
/// accepted inbound REFER (RFC 3515 §2.4 / RFC 6665). Application reports are translated into <c>message/sipfrag</c>
/// NOTIFYs on the dialog. The handle is created before the transfer callback runs, so the application may report
/// synchronously inside the handler; such reports are buffered until <see cref="StartAsync"/> flushes them after the
/// 202 has been sent. Later (asynchronous) reports are dispatched immediately.
/// </summary>
/// <remarks>
/// NOTIFY ordering is preserved by chaining every dispatch onto a single tail task under <c>_gate</c>; the CSeq is
/// assigned when a chained send actually runs, so it stays monotonic in wire order. The send delegate
/// (<see cref="SipReferHandler"/>'s NOTIFY sender) swallows its own transport failures, so the chain never faults
/// and one lost NOTIFY does not abort the subscription. Once started, an auto-timeout (the advertised 60 s
/// lifetime) fires a <c>terminated;reason=timeout</c> NOTIFY if the application never reports a terminal outcome;
/// a terminal report, <see cref="Cancel"/>, or the owning session's teardown cancels it.
/// </remarks>
internal sealed class SipReferSubscription : IReferSubscription
{
    // The advertised subscription lifetime; the auto-timeout matches it so the final NOTIFY lands as the
    // transferor's subscription expires (RFC 6665 §4.1.3).
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private const string PendingState = "pending;expires=60";
    private const string ActiveState = "active;expires=60";
    // RFC 6665 §4.1.3: a REFER subscription that has run its course reports "noresource" — the referenced
    // progress state no longer exists. Retained as the conventional REFER-completion reason.
    private const string TerminatedState = "terminated;reason=noresource";
    // RFC 6665 §4.1.3: the subscription expired without the application resolving the referred call.
    private const string TimeoutState = "terminated;reason=timeout";

    private readonly Func<string, string, CancellationToken, Task> _sendNotify;
    private readonly TimeSpan _timeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationToken _sessionShutdown;
    private readonly object _gate = new();
    private readonly List<(string SubState, string Sipfrag)> _pending = [];
    private Task _sendTail = Task.CompletedTask;
    private CancellationTokenSource? _timeoutCts;
    private Task? _timeoutTask;
    private Phase _phase = Phase.Created;
    private bool _startPending;

    private enum Phase
    {
        /// <summary>Handle created; reports are buffered until <see cref="StartAsync"/>.</summary>
        Created,
        /// <summary>Subscription live; reports dispatch immediately.</summary>
        Started,
        /// <summary>A terminal report has been made; further reports are ignored.</summary>
        Terminated,
        /// <summary>Declined or suppressed (RFC 4488); all reports are ignored and nothing is sent.</summary>
        Cancelled,
    }

    /// <summary>
    /// Creates the handle bound to a NOTIFY sender. <paramref name="sendNotify"/> takes the
    /// <c>Subscription-State</c> header value and the sipfrag body and must not throw. <paramref name="timeout"/>
    /// and <paramref name="delay"/> are injectable for testing; production uses the advertised 60 s lifetime and
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. <paramref name="sessionShutdown"/> cancels the
    /// auto-timeout when the owning session is torn down, so it never fires into a dead dialog.
    /// </summary>
    public SipReferSubscription(
        Func<string, string, CancellationToken, Task> sendNotify,
        TimeSpan? timeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken sessionShutdown = default)
    {
        _sendNotify = sendNotify;
        _timeout = timeout ?? DefaultTimeout;
        _delay = delay ?? Task.Delay;
        _sessionShutdown = sessionShutdown;
    }

    /// <inheritdoc />
    public void ReportPending()
    {
        lock (_gate)
        {
            // Only meaningful before the immediate NOTIFY is sent (i.e. in-handler); no-op once started/terminated.
            if (_phase == Phase.Created) _startPending = true;
        }
    }

    /// <inheritdoc />
    public void ReportTrying() => ReportProgress(100, "Trying");

    /// <inheritdoc />
    public void ReportRinging() => ReportProgress(180, "Ringing");

    /// <inheritdoc />
    public void ReportProgress(int statusCode, string? reasonPhrase = null)
    {
        var sipfrag = BuildSipfrag(statusCode, reasonPhrase);
        lock (_gate)
        {
            if (_phase is Phase.Terminated or Phase.Cancelled) return;
            if (_phase == Phase.Created) { _pending.Add((ActiveState, sipfrag)); return; }
            Dispatch(ActiveState, sipfrag);
        }
    }

    /// <inheritdoc />
    public void ReportSuccess(int statusCode = 200, string? reasonPhrase = null)
        => Terminate(statusCode, reasonPhrase);

    /// <inheritdoc />
    public void ReportFailure(int statusCode, string? reasonPhrase = null)
        => Terminate(statusCode, reasonPhrase);

    private void Terminate(int statusCode, string? reasonPhrase)
    {
        var sipfrag = BuildSipfrag(statusCode, reasonPhrase);
        lock (_gate)
        {
            if (_phase is Phase.Terminated or Phase.Cancelled) return;
            if (_phase == Phase.Created) { _pending.Add((TerminatedState, sipfrag)); _phase = Phase.Terminated; return; }
            _phase = Phase.Terminated;
            CancelTimeout();
            Dispatch(TerminatedState, sipfrag);
        }
    }

    /// <summary>
    /// Sends the immediate 100 Trying NOTIFY (RFC 3515 §2.4.4) — <c>pending</c> when the consumer signalled it
    /// in-handler via <see cref="ReportPending"/>, otherwise <c>active</c> — and flushes any reports the
    /// application made synchronously inside the transfer handler, in order. Returns a task that completes when
    /// those NOTIFYs have been dispatched. No-op once <see cref="Cancel"/> has run.
    /// </summary>
    internal Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_phase == Phase.Cancelled) return _sendTail;

            // RFC 6665 §4.1.3: start pending when the consumer signalled it in-handler, else active. A later
            // progress report (buffered below or dispatched after Start) carries the active state.
            Dispatch(_startPending ? PendingState : ActiveState, "SIP/2.0 100 Trying", ct);
            foreach (var (subState, sipfrag) in _pending)
                Dispatch(subState, sipfrag, ct);
            _pending.Clear();

            // Stay Terminated if a terminal was buffered in-handler; otherwise go live for later reports and arm
            // the auto-timeout so an accepted-but-never-resolved transfer still terminates the subscription.
            if (_phase == Phase.Created) _phase = Phase.Started;
            if (_phase == Phase.Started) ArmTimeout();
            return _sendTail;
        }
    }

    /// <summary>
    /// Marks the subscription declined or suppressed: discards buffered reports and ignores all future reports so
    /// nothing is sent on this handle. Used for a declined REFER (the single 603 NOTIFY is sent separately) and for
    /// RFC 4488 <c>Refer-Sub: false</c> suppression.
    /// </summary>
    internal void Cancel()
    {
        lock (_gate)
        {
            _pending.Clear();
            _phase = Phase.Cancelled;
            CancelTimeout();
        }
    }

    // Called under _gate. Arms the background auto-timeout; a terminal report or Cancel cancels it.
    private void ArmTimeout()
    {
        _timeoutCts = new CancellationTokenSource();
        _timeoutTask = RunTimeoutAsync(_timeoutCts.Token);
    }

    // Called under _gate. A plain CTS holds no unmanaged resource here (no CancelAfter, no WaitHandle), so it is
    // left for the GC rather than disposed — avoids a dispose/cancel race with the background timeout task.
    private void CancelTimeout() => _timeoutCts?.Cancel();

    private async Task RunTimeoutAsync(CancellationToken ct)
    {
        // Session teardown cancels the armed timeout so it never fires into a dead dialog. The registration is
        // scoped to this task (disposed on exit), so a completed/terminated subscription leaves nothing on the
        // long-lived session token.
        using var shutdownLink = _sessionShutdown.CanBeCanceled
            ? _sessionShutdown.Register(static s => ((CancellationTokenSource)s!).Cancel(), _timeoutCts)
            : default;

        try { await _delay(_timeout, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        Task tail;
        lock (_gate)
        {
            if (_phase is Phase.Terminated or Phase.Cancelled) return;
            _phase = Phase.Terminated;
            Dispatch(TimeoutState, "SIP/2.0 408 Request Timeout", ct);
            tail = _sendTail;
        }
        await tail.ConfigureAwait(false);
    }

    /// <summary>Test-only: awaits the auto-timeout task (completes when it fires-and-sends or is cancelled).</summary>
    internal Task WaitForTimeoutAsync()
    {
        lock (_gate) return _timeoutTask ?? Task.CompletedTask;
    }

    /// <summary>Test-only: awaits the current NOTIFY send chain so dispatched sends have settled.</summary>
    internal Task WaitForSendsAsync()
    {
        lock (_gate) return _sendTail;
    }

    private void Dispatch(string subState, string sipfrag, CancellationToken ct = default)
    {
        // Called under _gate. Chain onto the tail so NOTIFYs go out strictly in report order; _sendNotify
        // never faults, so the chain is safe to extend indefinitely.
        _sendTail = _sendTail.ContinueWith(
                _ => _sendNotify(subState, sipfrag, ct),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default)
            .Unwrap();
    }

    private static string BuildSipfrag(int statusCode, string? reasonPhrase)
    {
        var phrase = string.IsNullOrWhiteSpace(reasonPhrase)
            ? SipCallSessionUtilities.ResolveDefaultReasonPhrase(statusCode)
            : reasonPhrase;
        return $"SIP/2.0 {statusCode} {phrase}";
    }
}
