using System.Net;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Owns the live ICE checklist for one media component. Ordinary, triggered and nominating checks are submitted
/// to a shared <see cref="IceConnectivityCheckPacer"/> instead of being awaited in batches: transactions overlap,
/// but their start times remain globally paced (RFC 8445 §6.1.4.2, §7.3.1.4 and §14).
/// <para>
/// Both ICE roles run ordinary checks. Only the controlling role performs regular nomination, and it does so
/// exactly once after the highest-priority validated pair has no higher Waiting/In-Progress competitor
/// (RFC 8445 §8.1.1). A later candidate is still checked before nomination; after nomination, changing pairs
/// requires an ICE restart rather than an unnegotiated second USE-CANDIDATE.
/// </para>
/// </summary>
internal sealed class IceNominationDriver : IAsyncDisposable
{
    private const int DefaultMaxPairs = 256;

    private readonly List<IceLocalCandidate> _localCandidates;
    private readonly List<IceRemoteCandidate> _remotes = [];
    private readonly List<IceNominationPairState> _pairs = [];
    private readonly Action<IceLocalCandidate, IPEndPoint> _onNominated;
    private readonly IceConnectivityCheckPacer _pacer;
    private readonly bool _ownsPacer;
    private readonly int _maxNominationAttempts;
    private readonly int _maxPairs;
    private readonly ILogger<IceNominationDriver> _logger;
    private readonly object _gate = new();
    private bool _controlling;
    private bool _started;
    private bool _nominated;
    private bool _disposed;

    /// <summary>Creates a dynamic, bounded checklist over local × remote candidates.</summary>
    public IceNominationDriver(
        IReadOnlyList<IceLocalCandidate> localCandidates,
        IReadOnlyList<IceRemoteCandidate> remoteCandidates,
        Action<IceLocalCandidate, IPEndPoint> onNominated,
        ILoggerFactory loggerFactory,
        int maxAttempts = 3,
        TimeSpan? roundDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IceConnectivityCheckPacer? pacer = null,
        bool controlling = true,
        int maxPairs = DefaultMaxPairs)
    {
        ArgumentNullException.ThrowIfNull(localCandidates);
        ArgumentNullException.ThrowIfNull(remoteCandidates);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _localCandidates = [.. localCandidates];
        _onNominated = onNominated ?? throw new ArgumentNullException(nameof(onNominated));
        _maxNominationAttempts = maxAttempts > 0
            ? maxAttempts
            : throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _maxPairs = maxPairs > 0 ? maxPairs : throw new ArgumentOutOfRangeException(nameof(maxPairs));
        _controlling = controlling;
        _logger = loggerFactory.CreateLogger<IceNominationDriver>();
        _pacer = pacer ?? new IceConnectivityCheckPacer(loggerFactory, roundDelay, delay);
        _ownsPacer = pacer is null;

        foreach (var remote in remoteCandidates)
            AddRemoteLocked(remote);
    }

    /// <summary>Starts the pacer and submits every initial Waiting pair. Idempotent and thread-safe.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
                return;
            _started = true;
            foreach (var pair in _pairs)
                pair.Phase = IceNominationPairPhase.Waiting;
            QueueOrdinaryChecksLocked();
        }

        _pacer.Start();
    }

    /// <summary>Adds and schedules a trickled remote candidate, bounded by the checklist pair cap.</summary>
    public void AddCandidate(IceRemoteCandidate candidate)
    {
        lock (_gate)
        {
            if (_disposed || _nominated)
                return;
            AddRemoteLocked(candidate);
            if (_started)
                QueueOrdinaryChecksLocked();
        }
    }

    /// <summary>Adds a late local candidate (for example a TURN allocation) and pairs it with known remotes.</summary>
    public void AddLocalCandidate(IceLocalCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (_disposed || _nominated)
                return;
            _localCandidates.Add(candidate);
            foreach (var remote in _remotes)
                AddPairLocked(candidate, remote);
            if (_started)
                QueueOrdinaryChecksLocked();
        }
    }

    /// <summary>
    /// Learns a peer-reflexive remote candidate and queues its confirming check ahead of ordinary work
    /// (RFC 8445 §7.3.1.3–4). The supplied delegate preserves the exact direct/relay path the request used.
    /// </summary>
    public bool EnqueueTriggered(
        IPEndPoint source,
        long remotePriority,
        bool relayed,
        Func<CancellationToken, Task<bool>> check)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(check);

        lock (_gate)
        {
            if (_disposed)
                return false;

            var remote = AddRemoteLocked(new IceRemoteCandidate(source, remotePriority));
            var pair = SelectTriggeredPairLocked(remote, relayed);
            if (pair is null)
                return false;

            pair.PendingChecks++;
            if (!pair.Validated)
                pair.Phase = IceNominationPairPhase.InProgress;

            var queued = _pacer.TryEnqueue(new IceConnectivityCheckWork
            {
                Kind = IceConnectivityCheckKind.Triggered,
                Priority = pair.PairPriority,
                Execute = check,
                Complete = succeeded => OnConnectivityCheckCompleted(pair, succeeded),
            });
            if (!queued)
            {
                pair.PendingChecks--;
                if (!pair.Validated && pair.PendingChecks == 0)
                    pair.Phase = IceNominationPairPhase.Waiting;
                _logger.LogWarning("Dropping triggered ICE check for {Source}: the bounded pacer queue is full.", source);
            }
            return queued;
        }
    }

    /// <summary>Updates the role after RFC 8445 role-conflict resolution.</summary>
    public void SetRole(bool controlling)
    {
        lock (_gate)
        {
            if (_disposed || _controlling == controlling)
                return;

            _controlling = controlling;
            foreach (var pair in _pairs)
                pair.PairPriority = ComputePairPriority(pair.Local.Priority, pair.Remote.Priority, controlling);

            if (!controlling)
                CancelQueuedNominationLocked();
            else
                TryQueueNominationLocked();
        }
    }

    /// <summary>Stops checklist selection after the controlled role accepts the peer's nomination.</summary>
    public void AcceptRemoteNomination(IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        lock (_gate)
        {
            if (_disposed || _nominated)
                return;
            _nominated = true;
            CancelQueuedNominationLocked();
            var selected = _pairs
                .Where(pair => pair.Remote.EndPoint.Equals(remote))
                .OrderByDescending(pair => pair.PairPriority)
                .FirstOrDefault();
            if (selected is not null)
                selected.Phase = IceNominationPairPhase.Nominated;
        }
    }

    private IceRemoteCandidate AddRemoteLocked(IceRemoteCandidate candidate)
    {
        var existingIndex = _remotes.FindIndex(remote => remote.EndPoint.Equals(candidate.EndPoint));
        if (existingIndex >= 0)
        {
            var existing = _remotes[existingIndex];
            if (candidate.Priority <= existing.Priority)
                return existing;

            _remotes[existingIndex] = candidate;
            foreach (var pair in _pairs.Where(pair => pair.Remote.EndPoint.Equals(candidate.EndPoint)))
            {
                pair.Remote = candidate;
                pair.PairPriority = ComputePairPriority(pair.Local.Priority, candidate.Priority, _controlling);
            }
            return candidate;
        }

        _remotes.Add(candidate);
        foreach (var local in _localCandidates)
            AddPairLocked(local, candidate);
        return candidate;
    }

    private void AddPairLocked(IceLocalCandidate local, IceRemoteCandidate remote)
    {
        if (_pairs.Count >= _maxPairs)
        {
            _logger.LogWarning("ICE checklist pair cap {MaxPairs} reached; ignoring candidate pair for {Remote}.", _maxPairs, remote.EndPoint);
            return;
        }
        if (_pairs.Any(pair => ReferenceEquals(pair.Local, local) && pair.Remote.EndPoint.Equals(remote.EndPoint)))
            return;

        _pairs.Add(new IceNominationPairState
        {
            Local = local,
            Remote = remote,
            PairPriority = ComputePairPriority(local.Priority, remote.Priority, _controlling),
            Phase = _started ? IceNominationPairPhase.Waiting : IceNominationPairPhase.Frozen,
        });
    }

    private IceNominationPairState? SelectTriggeredPairLocked(IceRemoteCandidate remote, bool relayed)
    {
        var desiredType = relayed ? "relay" : "host";
        return _pairs
            .Where(pair => pair.Remote.EndPoint.Equals(remote.EndPoint))
            .OrderByDescending(pair => string.Equals(pair.Local.Type, desiredType, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(pair => pair.PairPriority)
            .FirstOrDefault();
    }

    private void QueueOrdinaryChecksLocked()
    {
        if (!_started || _nominated)
            return;

        foreach (var pair in _pairs.OrderByDescending(pair => pair.PairPriority))
        {
            if (pair.OrdinaryScheduled || pair.Phase is IceNominationPairPhase.Failed or IceNominationPairPhase.Nominated)
                continue;

            var queued = _pacer.TryEnqueue(new IceConnectivityCheckWork
            {
                Kind = IceConnectivityCheckKind.Ordinary,
                Priority = pair.PairPriority,
                Execute = ct => ExecuteOrdinaryCheckAsync(pair, ct),
                Complete = succeeded => OnConnectivityCheckCompleted(pair, succeeded),
            });
            if (!queued)
                break;

            pair.OrdinaryScheduled = true;
            pair.PendingChecks++;
            pair.Phase = IceNominationPairPhase.InProgress;
        }
    }

    private Task<bool> ExecuteOrdinaryCheckAsync(IceNominationPairState pair, CancellationToken ct)
    {
        IceLocalCandidate local;
        IPEndPoint remote;
        lock (_gate)
        {
            if (_disposed || _nominated)
                return Task.FromResult(false);
            // Snapshot the send path and target under the gate: AddRemoteLocked can swap the (struct) Remote
            // to upgrade its priority while this check runs, so reading it lock-free could tear (RFC 8445 §7.3.1.4).
            local = pair.Local;
            remote = pair.Remote.EndPoint;
        }
        return local.Check(remote, false, ct);
    }

    private void OnConnectivityCheckCompleted(IceNominationPairState pair, bool succeeded)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (pair.PendingChecks > 0)
                pair.PendingChecks--;
            if (succeeded)
            {
                pair.Validated = true;
                if (pair.Phase != IceNominationPairPhase.Nominated)
                    pair.Phase = IceNominationPairPhase.Succeeded;
            }
            else if (!pair.Validated && pair.PendingChecks == 0)
            {
                pair.Phase = IceNominationPairPhase.Failed;
            }

            QueueOrdinaryChecksLocked();
            TryQueueNominationLocked();
        }
    }

    private void TryQueueNominationLocked()
    {
        if (!_started || !_controlling || _nominated || _disposed
            || _pairs.Any(pair => pair.Phase == IceNominationPairPhase.Nominating))
        {
            return;
        }

        var candidate = _pairs
            .Where(pair => pair.Validated && pair.Phase == IceNominationPairPhase.Succeeded)
            .OrderByDescending(pair => pair.PairPriority)
            .FirstOrDefault();
        if (candidate is null)
            return;

        // DECISION: regular nomination (RFC 8445 §8.1.1), not aggressive. We nominate the highest-priority
        // validated pair only once no higher-priority pair is still in flight, so an unresolved high-priority
        // pair delays nomination by its transaction budget (~2 s worst case, bounded by the consent-check
        // retransmit schedule). This trades a bounded setup delay for always selecting the best working pair;
        // an aggressive policy would nominate the first validated pair sooner but risk locking onto a
        // suboptimal path. Revisit if setup latency on lossy high-priority pairs outweighs pair optimality.
        var higherPending = _pairs.Any(pair =>
            pair.PairPriority > candidate.PairPriority
            && pair.Phase is IceNominationPairPhase.Frozen
                or IceNominationPairPhase.Waiting
                or IceNominationPairPhase.InProgress);
        if (higherPending)
            return;

        candidate.Phase = IceNominationPairPhase.Nominating;
        var generation = ++candidate.NominationGeneration;
        if (!_pacer.TryEnqueue(new IceConnectivityCheckWork
        {
            Kind = IceConnectivityCheckKind.Nomination,
            Priority = candidate.PairPriority,
            Execute = ct => ExecuteNominationAsync(candidate, generation, ct),
            Complete = succeeded => OnNominationCompleted(candidate, generation, succeeded),
        }))
        {
            candidate.Phase = IceNominationPairPhase.Succeeded;
        }
    }

    private Task<bool> ExecuteNominationAsync(IceNominationPairState pair, int generation, CancellationToken ct)
    {
        IceLocalCandidate local;
        IPEndPoint remote;
        lock (_gate)
        {
            if (_disposed || _nominated || !_controlling
                || pair.Phase != IceNominationPairPhase.Nominating
                || pair.NominationGeneration != generation)
            {
                return Task.FromResult(false);
            }
            // Snapshot under the gate for the same reason as ExecuteOrdinaryCheckAsync (RFC 8445 §7.3.1.4).
            local = pair.Local;
            remote = pair.Remote.EndPoint;
        }
        return local.Check(remote, true, ct);
    }

    private void OnNominationCompleted(IceNominationPairState pair, int generation, bool succeeded)
    {
        bool raise = false;
        lock (_gate)
        {
            if (_disposed || _nominated || pair.Phase != IceNominationPairPhase.Nominating
                || pair.NominationGeneration != generation)
            {
                return;
            }

            if (succeeded)
            {
                pair.Phase = IceNominationPairPhase.Nominated;
                _nominated = true;
                raise = true;
            }
            else
            {
                pair.NominationAttempts++;
                pair.Phase = pair.NominationAttempts >= _maxNominationAttempts
                    ? IceNominationPairPhase.Failed
                    : IceNominationPairPhase.Succeeded;
                TryQueueNominationLocked();
            }
        }

        if (!raise)
            return;

        _logger.LogDebug(
            "ICE nominated pair local={LocalType} remote={Remote} priority={Priority} after confirmed USE-CANDIDATE.",
            pair.Local.Type, pair.Remote.EndPoint, pair.PairPriority);
        RaiseNominated(pair.Local, pair.Remote.EndPoint);
    }

    private void CancelQueuedNominationLocked()
    {
        foreach (var pair in _pairs.Where(pair => pair.Phase == IceNominationPairPhase.Nominating))
        {
            pair.NominationGeneration++;
            pair.Phase = pair.Validated ? IceNominationPairPhase.Succeeded : IceNominationPairPhase.Waiting;
        }
    }

    private void RaiseNominated(IceLocalCandidate local, IPEndPoint remote)
    {
        try
        {
            _onNominated(local, remote);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in ICE nomination handler.");
        }
    }

    private static long ComputePairPriority(long localPriority, long remotePriority, bool controlling)
    {
        var g = controlling ? localPriority : remotePriority;
        var d = controlling ? remotePriority : localPriority;
        var min = Math.Min(g, d);
        var max = Math.Max(g, d);
        return (min << 32) + (max << 1) + (g > d ? 1 : 0);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        if (_ownsPacer)
            await _pacer.DisposeAsync().ConfigureAwait(false);
    }
}
