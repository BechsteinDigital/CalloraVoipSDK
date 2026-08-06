using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Bounded single-writer egress for the DTLS handshake/keying channel (#191). BouncyCastle sends
/// records synchronously from its handshake thread; this pump enqueues them onto a bounded queue
/// drained by one consumer that awaits the async socket send in order. Records therefore keep
/// their local order and cannot race each other — DTLS tolerates network reordering (RFC 6347
/// §4.2.7), but the local bridge must not add its own. A transport failure is captured and
/// re-thrown from the next <see cref="Enqueue"/> so it reaches BouncyCastle and fails the
/// handshake closed instead of only being logged. Teardown drains a pending close_notify with a
/// tight deadline (<see cref="DrainAsync"/>) before the caller cancels the rest via
/// <see cref="DisposeAsync"/> (ENGINEERING_RULES K3; HARD-F4 bounded queue with backpressure).
/// </summary>
internal sealed class DtlsEgressPump : IAsyncDisposable
{
    // Bounded so a stuck socket cannot grow memory without limit. Add blocks when full — the local
    // backpressure point on the BC handshake thread, not a second unbounded queue. A single DTLS
    // flight is a handful of records, so 64 leaves ample headroom before backpressure engages.
    private const int DefaultCapacity = 64;

    private readonly Func<byte[], CancellationToken, ValueTask> _send;
    private readonly ILogger _logger;
    private readonly BlockingCollection<byte[]> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private volatile Exception? _fault;
    private int _disposed;

    public DtlsEgressPump(
        Func<byte[], CancellationToken, ValueTask> send,
        ILogger logger,
        int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _send = send;
        _logger = logger;
        _queue = new BlockingCollection<byte[]>(capacity);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Completes when the consumer loop has stopped — drained, faulted, or cancelled.</summary>
    public Task Completion => _pump;

    /// <summary>
    /// Hands one datagram to the single-writer consumer, blocking briefly when the bounded queue is
    /// full (backpressure). Re-throws a prior transport failure so the BouncyCastle handshake thread
    /// aborts the handshake fail-closed instead of losing the error to a log line.
    /// </summary>
    /// <exception cref="IOException">A previous egress send failed.</exception>
    public void Enqueue(byte[] datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);

        var fault = _fault;
        if (fault is not null)
            throw new IOException("DTLS egress transport failed.", fault);

        try
        {
            _queue.Add(datagram);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding ran (teardown or a faulted consumer) — drop this record. A live
            // handshake retransmits; a faulted one surfaces on the next Enqueue via _fault.
            _logger.LogTrace("DTLS egress dropped a record; the pump is closing.");
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            foreach (var datagram in _queue.GetConsumingEnumerable())
            {
                try
                {
                    await _send(datagram, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // Teardown cancelled the remaining egress after the close_notify deadline.
                    return;
                }
                catch (Exception ex)
                {
                    // First transport fault: capture it so the next Enqueue throws it into the
                    // handshake, and stop consuming (the handshake is about to fail closed).
                    // CompleteAdding unblocks any producer parked on a full queue.
                    _fault = ex;
                    if (!_queue.IsAddingCompleted)
                        _queue.CompleteAdding();
                    _logger.LogWarning(ex, "DTLS egress send failed; failing the handshake closed.");
                    return;
                }
            }
        }
        catch (ObjectDisposedException ex)
        {
            // The queue was disposed while the consumer was parked on it during teardown.
            _logger.LogTrace(ex, "DTLS egress pump observed queue disposal during teardown.");
        }
    }

    /// <summary>
    /// Stops accepting new datagrams and waits, up to <paramref name="deadline"/>, for the queued
    /// records (typically a just-enqueued close_notify) to actually flush. Does not cancel: the
    /// caller cancels the remaining egress afterwards via <see cref="DisposeAsync"/>. The deadline
    /// bounds the wait so a dead socket cannot stall teardown.
    /// </summary>
    public async Task DrainAsync(TimeSpan deadline)
    {
        if (!_queue.IsAddingCompleted)
            _queue.CompleteAdding();

        var finished = await Task.WhenAny(_pump, Task.Delay(deadline)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, _pump))
            _logger.LogDebug("DTLS egress drain deadline elapsed before all records flushed.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (!_queue.IsAddingCompleted)
            _queue.CompleteAdding();

        _cts.Cancel();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // PumpAsync observes its own faults; anything escaping here is a teardown race and
            // must not break disposal.
            _logger.LogDebug(ex, "DTLS egress pump faulted during disposal.");
        }

        _cts.Dispose();
        _queue.Dispose();
    }
}
