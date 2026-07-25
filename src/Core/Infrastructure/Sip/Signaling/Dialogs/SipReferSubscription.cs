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
/// (<see cref="SipCallSessionInboundService"/>'s NOTIFY sender) swallows its own transport failures, so the chain
/// never faults and one lost NOTIFY does not abort the subscription.
/// </remarks>
internal sealed class SipReferSubscription : IReferSubscription
{
    private const string ActiveState = "active;expires=60";
    // RFC 6665 §4.1.3: a REFER subscription that has run its course reports "noresource" — the referenced
    // progress state no longer exists. Retained as the conventional REFER-completion reason.
    private const string TerminatedState = "terminated;reason=noresource";

    private readonly Func<string, string, CancellationToken, Task> _sendNotify;
    private readonly object _gate = new();
    private readonly List<(string SubState, string Sipfrag)> _pending = [];
    private Task _sendTail = Task.CompletedTask;
    private Phase _phase = Phase.Created;

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
    /// <c>Subscription-State</c> header value and the sipfrag body and must not throw.
    /// </summary>
    public SipReferSubscription(Func<string, string, CancellationToken, Task> sendNotify)
        => _sendNotify = sendNotify;

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
            Dispatch(TerminatedState, sipfrag);
        }
    }

    /// <summary>
    /// Sends the immediate <c>active</c>/100 Trying NOTIFY (RFC 3515 §2.4.4) and flushes any reports the
    /// application made synchronously inside the transfer handler, in order. Returns a task that completes when
    /// those NOTIFYs have been dispatched. No-op once <see cref="Cancel"/> has run.
    /// </summary>
    internal Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_phase == Phase.Cancelled) return _sendTail;

            Dispatch(ActiveState, "SIP/2.0 100 Trying", ct);
            foreach (var (subState, sipfrag) in _pending)
                Dispatch(subState, sipfrag, ct);
            _pending.Clear();

            // Stay Terminated if a terminal was buffered in-handler; otherwise go live for later reports.
            if (_phase == Phase.Created) _phase = Phase.Started;
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
        }
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
