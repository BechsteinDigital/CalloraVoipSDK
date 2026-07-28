using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Timing;

/// <summary>
/// Low-allocation shared timer scheduler using a single worker loop and priority queue.
/// Designed to replace many per-operation <see cref="Task.Delay(TimeSpan)"/> timers.
/// </summary>
internal sealed class ScheduledActionScheduler : IScheduledActionScheduler
{
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly PriorityQueue<ScheduledActionEntry, long> _queue = new();
    private readonly Dictionary<long, ScheduledActionEntry> _entriesById = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private long _nextId;
    private int _disposed;

    /// <summary>
    /// Creates a scheduler instance and starts its background dispatch loop.
    /// </summary>
    public ScheduledActionScheduler(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loop = Task.Run(() => DispatchLoopAsync(_stop.Token));
    }

    /// <inheritdoc />
    public IDisposable Schedule(
        TimeSpan delay,
        Action callback)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ScheduledActionScheduler));
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        ArgumentNullException.ThrowIfNull(callback);

        var id = Interlocked.Increment(ref _nextId);
        var dueAtTicks = DateTimeOffset.UtcNow.Add(delay).UtcTicks;
        var entry = new ScheduledActionEntry(
            id,
            dueAtTicks,
            callback);

        lock (_sync)
        {
            _entriesById[id] = entry;
            _queue.Enqueue(entry, dueAtTicks);
        }

        _signal.Release();
        return new ScheduledActionHandle(id, Cancel);
    }

    /// <summary>
    /// Cancels one scheduled callback by identifier.
    /// </summary>
    private void Cancel(long id)
    {
        bool affectsNextDue;
        lock (_sync)
        {
            if (!_entriesById.TryGetValue(id, out var entry))
                return;

            entry.IsCanceled = true;

            // The worker's current wait is derived from the queue head. Cancelling a non-head
            // entry never shortens the required wait (the head is still due first) and never
            // strands a still-due entry (the worker re-scans and skips cancelled entries on its
            // next wake), so only a head cancellation must wake the worker to re-evaluate.
            affectsNextDue = _queue.Count > 0 && _queue.Peek().Id == id;
        }

        if (!affectsNextDue)
            return;

        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Scheduled action signal was already disposed during cancellation.");
        }
    }

    /// <summary>
    /// Runs scheduler loop and dispatches due callbacks.
    /// </summary>
    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!TryGetDelayUntilNextDue(out var wait))
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (wait > TimeSpan.Zero)
                {
                    var signaled = await _signal.WaitAsync(wait, ct).ConfigureAwait(false);
                    if (signaled)
                        continue;
                }

                var dueEntries = DequeueDueEntries(DateTimeOffset.UtcNow.UtcTicks);
                foreach (var entry in dueEntries)
                    ExecuteCallbackSafely(entry.Callback);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "Scheduled action loop canceled.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled action loop failed unexpectedly.");
            }
        }
    }

    /// <summary>
    /// Returns delay until next due callback. False when queue is empty.
    /// </summary>
    private bool TryGetDelayUntilNextDue(out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        lock (_sync)
        {
            while (_queue.Count > 0)
            {
                var next = _queue.Peek();
                if (next.IsCanceled)
                {
                    _queue.Dequeue();
                    _entriesById.Remove(next.Id);
                    continue;
                }

                var dueAt = new DateTimeOffset(next.DueAtTicks, TimeSpan.Zero);
                var now = DateTimeOffset.UtcNow;
                delay = dueAt <= now ? TimeSpan.Zero : dueAt - now;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Dequeues all callbacks due at or before the provided timestamp.
    /// </summary>
    private IReadOnlyList<ScheduledActionEntry> DequeueDueEntries(long nowUtcTicks)
    {
        var due = new List<ScheduledActionEntry>();
        lock (_sync)
        {
            while (_queue.Count > 0)
            {
                var next = _queue.Peek();
                if (next.IsCanceled)
                {
                    _queue.Dequeue();
                    _entriesById.Remove(next.Id);
                    continue;
                }

                if (next.DueAtTicks > nowUtcTicks)
                    break;

                _queue.Dequeue();
                _entriesById.Remove(next.Id);
                due.Add(next);
            }
        }

        return due;
    }

    /// <summary>
    /// Executes one callback with exception-to-log safety.
    /// </summary>
    private void ExecuteCallbackSafely(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled callback execution failed.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop.Cancel();
        _signal.Release();

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scheduled action loop did not stop cleanly.");
        }

        lock (_sync)
        {
            _queue.Clear();
            _entriesById.Clear();
        }

        _stop.Dispose();
        _signal.Dispose();
    }

}
