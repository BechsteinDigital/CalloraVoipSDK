using System.Net;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Drives the controlling agent's ICE candidate-pair connectivity checks and nomination over the shared
/// media socket (RFC 8445 §7.2.2 checks, §8.1.1 regular nomination) as a long-lived, dynamic check list.
/// It always works on the highest-priority candidate pair still worth checking, sends an ordinary
/// connectivity check over that pair's <em>local</em> candidate send path (<see cref="IceLocalCandidate.Check"/>,
/// backed by the receive-loop-matched <see cref="IceMediaConsentSession.SendCheckAsync"/> for a direct
/// candidate, or a relay-framed path for a relay candidate), and — when a pair answers — sends a nominating
/// check carrying USE-CANDIDATE and, once that nominating check is itself confirmed by a success response,
/// reports the pair through <c>onNominated</c> so the caller redirects the media send target and consent
/// freshness to it. Nomination is gated on a confirmed USE-CANDIDATE response (RFC 8445 §8.1.1) — never on
/// raw priority, and never on the ordinary check alone (a lost USE-CANDIDATE is retried, not adopted).
/// <para>
/// Pairs are formed over local × remote candidates (RFC 8445 §6.1.2) and ordered by pair priority (§6.1.2.3):
/// a lower-preference local candidate such as a relay is therefore only nominated when no higher-preference
/// (host/srflx) pair works, matching direct-preferred ICE. Remote candidates that arrive after start
/// (RFC 8838 trickle) are fed in via <see cref="AddCandidate"/> and paired against every local candidate. If
/// a higher-priority pair than the current nominee later answers, the driver re-nominates onto it (§8): the
/// selection is always the highest-priority <em>working</em> pair, not the highest-priority advertised one. A
/// pair that does not answer is retried a bounded number of times before being abandoned.
/// </para>
/// <para>
/// Receive-loop integration is what makes this non-facade: checks and their responses share the one media
/// socket the transport owns after start, matched by transaction id, while the peer's inbound handler
/// answers our checks — so it works while both agents are up (no pre-start bootstrapping deadlock). Only the
/// controlling agent runs a driver; the controlled agent adopts the nominated pair from the peer's
/// USE-CANDIDATE check (RFC 8445 §7.3.1.5).
/// </para>
/// </summary>
internal sealed class IceNominationDriver : IAsyncDisposable
{
    // Local candidates and the remotes seen so far are both mutable (guarded by _gate): a local candidate can
    // be added after construction (AddLocalCandidate — the answerer's late relay path), and each must pair
    // against every remote known then plus every remote that trickles in later, so both sides of the cross
    // product are retained rather than only the formed pairs.
    private readonly List<IceLocalCandidate> _localCandidates;
    private readonly List<IceRemoteCandidate> _remotes = [];
    private readonly Action<IceLocalCandidate, IPEndPoint> _onNominated;
    private readonly int _maxAttempts;
    private readonly TimeSpan _roundDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<IceNominationDriver> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<IceNominationPairState> _pairs = [];
    private readonly SemaphoreSlim _signal = new(0);
    private long _nominatedPriority = long.MinValue; // guarded by _gate
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Creates the nomination driver: it pairs the local candidates against the initial remote candidates
    /// (from the SDP) and checks the pairs highest-priority first.
    /// </summary>
    /// <param name="localCandidates">
    /// The local candidates (send paths) to pair against every remote candidate. Typically the direct
    /// host/srflx path, plus a relay path when a TURN allocation exists.
    /// </param>
    /// <param name="remoteCandidates">The initial remote candidates to check.</param>
    /// <param name="onNominated">
    /// Invoked with the nominated pair's local candidate and remote endpoint whenever a pair is selected or
    /// upgraded, so the caller can redirect media/consent to that pair (and, for a relay local candidate,
    /// switch the transport to the relay data path).
    /// </param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="maxAttempts">How many checks a single pair is given before it is abandoned.</param>
    /// <param name="roundDelay">Delay between check attempts; also the idle poll interval; injectable for tests.</param>
    /// <param name="delay">The delay primitive; injectable for deterministic tests.</param>
    public IceNominationDriver(
        IReadOnlyList<IceLocalCandidate> localCandidates,
        IReadOnlyList<IceRemoteCandidate> remoteCandidates,
        Action<IceLocalCandidate, IPEndPoint> onNominated,
        ILoggerFactory loggerFactory,
        int maxAttempts = 5,
        TimeSpan? roundDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(localCandidates);
        _localCandidates = [.. localCandidates];
        ArgumentNullException.ThrowIfNull(remoteCandidates);
        _onNominated = onNominated ?? throw new ArgumentNullException(nameof(onNominated));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _maxAttempts = maxAttempts > 0 ? maxAttempts : throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _roundDelay = roundDelay ?? TimeSpan.FromMilliseconds(200);
        _delay = delay ?? Task.Delay;
        _logger = loggerFactory.CreateLogger<IceNominationDriver>();
        foreach (var remote in remoteCandidates)
            AddPairsForRemote(remote);
    }

    /// <summary>
    /// Starts the check/nomination worker on a background task. Idempotent and thread-safe; a second call
    /// or a call after disposal is a no-op. Safe to call after seeding or adding candidates.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed)
                return;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    /// <summary>
    /// Adds a remote candidate discovered after construction (RFC 8838 trickle). It is paired against every
    /// local candidate and connectivity-checked like any other pair, and can re-nominate if a resulting pair
    /// is higher priority than the current nominee and answers. No-op after disposal. Safe to call before or
    /// after <see cref="Start"/>.
    /// </summary>
    /// <param name="candidate">The trickled remote candidate.</param>
    public void AddCandidate(IceRemoteCandidate candidate)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            AddPairsForRemote(candidate);
            // Release inside the gate so the disposed-check and the release are atomic: DisposeAsync disposes
            // the semaphore only after setting _disposed under this same gate, so a release can never race a
            // disposed semaphore. Release does not block, so holding the lock is safe.
            _signal.Release();
        }
    }

    /// <summary>
    /// Adds a local candidate discovered after construction — the answerer's relay send path, whose TURN
    /// allocation only finished gathering once the session (and this driver) already existed, so it could not
    /// be seeded like the offerer's relay candidate. It is paired against every remote seen so far and against
    /// every remote that trickles in later, and connectivity-checked like any other pair (ordered by pair
    /// priority, so a lower-preference relay is only nominated when no direct pair works). No-op after disposal.
    /// Safe to call before or after <see cref="Start"/>.
    /// </summary>
    /// <param name="candidate">The local candidate (send path) to pair in.</param>
    public void AddLocalCandidate(IceLocalCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (_disposed)
                return;
            _localCandidates.Add(candidate);
            foreach (var remote in _remotes)
                AddPair(candidate, remote);
            // Release inside the gate for the same disposed-vs-release atomicity as AddCandidate.
            _signal.Release();
        }
    }

    // Records the remote and forms a pair between it and every local candidate. Caller holds _gate (or is the
    // constructor, before the instance is published).
    private void AddPairsForRemote(IceRemoteCandidate remote)
    {
        _remotes.Add(remote);
        foreach (var local in _localCandidates)
            AddPair(local, remote);
    }

    // Forms one candidate pair (RFC 8445 §6.1.2) at its computed pair priority (§6.1.2.3). Caller holds _gate
    // (or is the constructor).
    private void AddPair(IceLocalCandidate local, IceRemoteCandidate remote) =>
        _pairs.Add(new IceNominationPairState
        {
            Local = local,
            Remote = remote,
            PairPriority = ComputePairPriority(local.Priority, remote.Priority),
        });

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var next = SelectBestActionable();
                if (next is null)
                {
                    // Nothing worth checking right now — wait for a trickled candidate (signal) or poll after
                    // the round delay so a pair still within its retry budget is revisited.
                    await WaitForWorkAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (await next.Local.Check(next.Remote.EndPoint, false, ct).ConfigureAwait(false))
                {
                    // Validated pair (RFC 8445 §7.2.5). Nominate with a USE-CANDIDATE check (§8.1.1) and adopt
                    // the pair only once THAT check gets its own success response: the ordinary check proves the
                    // path works, but only the nominating check's response proves the controlled peer received
                    // the USE-CANDIDATE and marked the pair nominated. Adopting on a lost USE-CANDIDATE would let
                    // the controlling side switch its media/consent target locally while the peer still has no
                    // nominated pair — so a missed nomination is retried, not adopted.
                    if (await next.Local.Check(next.Remote.EndPoint, true, ct).ConfigureAwait(false))
                    {
                        lock (_gate)
                        {
                            _nominatedPriority = next.PairPriority;
                            next.Done = true;
                        }

                        _logger.LogDebug(
                            "ICE nominated pair local={LocalType} remote={EndPoint} (pair priority {Priority}) after a " +
                            "confirmed USE-CANDIDATE check (RFC 8445 §7.2.2/§8.1.1).",
                            next.Local.Type, next.Remote.EndPoint, next.PairPriority);
                        RaiseNominated(next.Local, next.Remote.EndPoint);
                    }
                    else
                    {
                        // The path validated but the nominating check was not confirmed (a lost USE-CANDIDATE
                        // request or its response). Count the attempt and retry the pair (a fresh ordinary check
                        // then USE-CANDIDATE on the next round) rather than adopting it.
                        bool exhausted;
                        lock (_gate)
                        {
                            next.Attempts++;
                            exhausted = next.Attempts >= _maxAttempts;
                            if (exhausted)
                                next.Done = true;
                        }

                        if (exhausted)
                            _logger.LogDebug(
                                "ICE nominating (USE-CANDIDATE) check for local={LocalType} remote={EndPoint} was not " +
                                "confirmed after {Attempts} attempts; abandoning nomination of it.",
                                next.Local.Type, next.Remote.EndPoint, _maxAttempts);

                        await _delay(_roundDelay, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    bool exhausted;
                    lock (_gate)
                    {
                        next.Attempts++;
                        exhausted = next.Attempts >= _maxAttempts;
                        if (exhausted)
                            next.Done = true;
                    }

                    if (exhausted)
                        _logger.LogDebug(
                            "ICE pair local={LocalType} remote={EndPoint} did not answer after {Attempts} checks; abandoning it.",
                            next.Local.Type, next.Remote.EndPoint, _maxAttempts);

                    // Pace retries and give lower-priority pairs a turn between attempts.
                    await _delay(_roundDelay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed / cancelled — stop quietly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ICE nomination driver loop failed unexpectedly; keeping the current remote.");
        }
    }

    // The highest-priority pair still worth a check: not finished, attempts remaining, and strictly higher
    // pair priority than the currently nominated pair (a working higher-priority pair upgrades the selection;
    // nothing at or below the nominated priority is worth checking).
    private IceNominationPairState? SelectBestActionable()
    {
        lock (_gate)
        {
            IceNominationPairState? best = null;
            foreach (var state in _pairs)
            {
                if (state.Done || state.Attempts >= _maxAttempts || state.PairPriority <= _nominatedPriority)
                    continue;
                if (best is null || state.PairPriority > best.PairPriority)
                    best = state;
            }

            return best;
        }
    }

    private void RaiseNominated(IceLocalCandidate local, IPEndPoint endPoint)
    {
        try
        {
            _onNominated(local, endPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in ICE nomination handler.");
        }
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        // Wake on a newly added candidate (signal) or after the round delay to re-check retriable pairs.
        try
        {
            await _signal.WaitAsync(_roundDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
    }

    // RFC 8445 §6.1.2.3 pair priority. The driver is the controlling agent (only controlling runs a driver),
    // so G is the local candidate priority and D the remote's. Realistic ICE candidate priorities are below
    // 2^31, so min<<32 stays within Int64 range.
    private static long ComputePairPriority(long controllingPriority, long controlledPriority)
    {
        var min = Math.Min(controllingPriority, controlledPriority);
        var max = Math.Max(controllingPriority, controlledPriority);
        return (min << 32) + (max << 1) + (controllingPriority > controlledPriority ? 1 : 0);
    }

    /// <summary>Cancels the loop and awaits its completion. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            loop = _loop;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        // The loop observes cancellation internally (every await returns on it), so awaiting it here does
        // not throw — mirrors IceConsentMonitor.DisposeAsync.
        if (loop is not null)
            await loop.ConfigureAwait(false);
        _cts.Dispose();
        _signal.Dispose();
    }
}
