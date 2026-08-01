using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Serialises the start of outbound ICE checks while allowing their STUN transactions to remain concurrently
/// in progress. At most one new check starts per pacing interval; triggered checks are FIFO-first, followed by
/// nomination and ordinary checks ordered by pair priority (RFC 8445 §6.1.4.2, §7.3.1.4 and §14).
/// </summary>
internal sealed class IceConnectivityCheckPacer : IAsyncDisposable
{
    private const int DefaultMaxQueuedChecks = 512;
    private static readonly TimeSpan DefaultPacingInterval = TimeSpan.FromMilliseconds(50);

    private readonly Queue<IceConnectivityCheckWork> _triggered = new();
    private readonly PriorityQueue<IceConnectivityCheckWork, long> _nominations = new();
    private readonly PriorityQueue<IceConnectivityCheckWork, long> _ordinary = new();
    private readonly HashSet<Task> _inFlight = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private readonly TimeSpan _pacingInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxQueuedChecks;
    private readonly ILogger<IceConnectivityCheckPacer> _logger;
    private Task? _loop;
    private int _queued;
    private bool _disposed;

    /// <summary>Creates a bounded global pacer for one ICE media attachment.</summary>
    public IceConnectivityCheckPacer(
        ILoggerFactory loggerFactory,
        TimeSpan? pacingInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxQueuedChecks = DefaultMaxQueuedChecks)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _pacingInterval = pacingInterval ?? DefaultPacingInterval;
        if (_pacingInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pacingInterval));
        _delay = delay ?? Task.Delay;
        _maxQueuedChecks = maxQueuedChecks > 0
            ? maxQueuedChecks
            : throw new ArgumentOutOfRangeException(nameof(maxQueuedChecks));
        _logger = loggerFactory.CreateLogger<IceConnectivityCheckPacer>();
    }

    /// <summary>Starts the pacing loop. Idempotent and thread-safe.</summary>
    public void Start()
    {
        lock (_gate)
            EnsureLoopStartedLocked();
    }

    // Starts the drain loop if it is not already running. Triggered checks (RFC 8445 §7.3.1.4) are enqueued
    // reactively off the receive loop and must dispatch even before the owning checklist calls Start(), so
    // enqueuing self-starts the loop rather than depending on an explicit Start(). Caller holds _gate.
    private void EnsureLoopStartedLocked()
    {
        if (_loop is not null || _disposed)
            return;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Enqueues one check without blocking its caller. Returns false after disposal or when the DoS cap is full.
    /// </summary>
    public bool TryEnqueue(IceConnectivityCheckWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_disposed || _queued >= _maxQueuedChecks)
                return false;

            var wake = _queued == 0;
            switch (work.Kind)
            {
                case IceConnectivityCheckKind.Triggered:
                    _triggered.Enqueue(work);
                    break;
                case IceConnectivityCheckKind.Nomination:
                    _nominations.Enqueue(work, ToQueuePriority(work.Priority));
                    break;
                default:
                    _ordinary.Enqueue(work, ToQueuePriority(work.Priority));
                    break;
            }

            _queued++;
            EnsureLoopStartedLocked();
            if (wake)
                _signal.Release();
            return true;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var work = Dequeue();
                if (work is null)
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                Track(ExecuteAsync(work, ct));
                await _delay(_pacingInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Dispose owns cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ICE connectivity-check pacer failed unexpectedly.");
        }
    }

    private IceConnectivityCheckWork? Dequeue()
    {
        lock (_gate)
        {
            IceConnectivityCheckWork? work = null;
            if (_triggered.Count > 0)
                work = _triggered.Dequeue();
            else if (_nominations.Count > 0)
                work = _nominations.Dequeue();
            else if (_ordinary.Count > 0)
                work = _ordinary.Dequeue();

            if (work is not null)
                _queued--;
            return work;
        }
    }

    private async Task ExecuteAsync(IceConnectivityCheckWork work, CancellationToken ct)
    {
        bool succeeded;
        try
        {
            succeeded = await work.Execute(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICE {Kind} connectivity check failed.", work.Kind);
            succeeded = false;
        }

        try
        {
            work.Complete(succeeded);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ICE {Kind} check completion handler failed.", work.Kind);
        }
    }

    private void Track(Task task)
    {
        lock (_gate)
            _inFlight.Add(task);

        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                    _inFlight.Remove(completed);
                if (completed.IsFaulted)
                    _logger.LogError(completed.Exception, "Unobserved ICE connectivity-check task failure.");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static long ToQueuePriority(long pairPriority) => pairPriority == long.MinValue ? long.MaxValue : -pairPriority;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            loop = _loop;
            _triggered.Clear();
            _nominations.Clear();
            _ordinary.Clear();
            _queued = 0;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
            await loop.ConfigureAwait(false);

        Task[] inFlight;
        lock (_gate)
            inFlight = [.. _inFlight];
        if (inFlight.Length > 0)
            await Task.WhenAll(inFlight).ConfigureAwait(false);

        _cts.Dispose();
        _signal.Dispose();
    }
}
