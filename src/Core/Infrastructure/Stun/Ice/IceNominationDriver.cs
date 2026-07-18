using System.Net;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Drives the controlling agent's ICE candidate-pair connectivity checks and nomination over the shared
/// media socket (RFC 8445 §7.2.2 checks, §8.1.1 regular nomination) as a long-lived, dynamic check list.
/// It always works on the highest-priority candidate still worth checking, sends an ordinary connectivity
/// check through the injected <c>check</c> delegate (backed by the receive-loop-matched
/// <see cref="IceMediaConsentSession.SendCheckAsync"/>), and — when a candidate answers — sends a nominating
/// check carrying USE-CANDIDATE and reports it through <c>onNominated</c> so the caller redirects the media
/// send target and consent freshness to it. Nomination is gated on a real answer, never on raw priority.
/// <para>
/// Candidates that arrive after start (RFC 8838 trickle) are fed in via <see cref="AddCandidate"/> and
/// checked like any other. If a higher-priority candidate than the current nominee later answers, the driver
/// re-nominates onto it (§8): the selection is always the highest-priority <em>working</em> pair, not the
/// highest-priority advertised one. A candidate that does not answer is retried a bounded number of times
/// (a peer's transport may still be coming up) before being abandoned.
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
    private readonly Func<IPEndPoint, bool, CancellationToken, Task<bool>> _check;
    private readonly Action<IPEndPoint> _onNominated;
    private readonly int _maxAttempts;
    private readonly TimeSpan _roundDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<IceNominationDriver> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<IceNominationCandidateState> _candidates = [];
    private readonly SemaphoreSlim _signal = new(0);
    private long _nominatedPriority = long.MinValue; // guarded by _gate
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Creates the nomination driver seeded with the initial remote candidates (from the SDP).
    /// </summary>
    /// <param name="candidates">The initial remote candidates to check (visited highest-priority first).</param>
    /// <param name="check">
    /// Sends one connectivity check to a target — <c>(target, useCandidate, ct)</c> — and returns
    /// <see langword="true"/> when a matching response arrives. Wired to the shared consent session so the
    /// check and its response ride the media socket's receive loop.
    /// </param>
    /// <param name="onNominated">Invoked with the nominated remote endpoint whenever a pair is selected or upgraded.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="maxAttempts">How many checks a single candidate is given before it is abandoned.</param>
    /// <param name="roundDelay">Delay between check attempts; also the idle poll interval; injectable for tests.</param>
    /// <param name="delay">The delay primitive; injectable for deterministic tests.</param>
    public IceNominationDriver(
        IReadOnlyList<IceRemoteCandidate> candidates,
        Func<IPEndPoint, bool, CancellationToken, Task<bool>> check,
        Action<IPEndPoint> onNominated,
        ILoggerFactory loggerFactory,
        int maxAttempts = 5,
        TimeSpan? roundDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _onNominated = onNominated ?? throw new ArgumentNullException(nameof(onNominated));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _maxAttempts = maxAttempts > 0 ? maxAttempts : throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _roundDelay = roundDelay ?? TimeSpan.FromMilliseconds(200);
        _delay = delay ?? Task.Delay;
        _logger = loggerFactory.CreateLogger<IceNominationDriver>();
        foreach (var candidate in candidates)
            _candidates.Add(new IceNominationCandidateState { Candidate = candidate });
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
    /// Adds a remote candidate discovered after construction (RFC 8838 trickle) to the check list. It is
    /// connectivity-checked like any other and can re-nominate the pair if it is higher priority than the
    /// current nominee and answers. No-op after disposal. Safe to call before or after <see cref="Start"/>.
    /// </summary>
    /// <param name="candidate">The trickled remote candidate.</param>
    public void AddCandidate(IceRemoteCandidate candidate)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _candidates.Add(new IceNominationCandidateState { Candidate = candidate });
        }

        _signal.Release();
    }

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
                    // the round delay so a candidate still within its retry budget is revisited.
                    await WaitForWorkAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (await _check(next.Candidate.EndPoint, false, ct).ConfigureAwait(false))
                {
                    // Validated pair (RFC 8445 §7.2.5). Send the nominating check carrying USE-CANDIDATE
                    // (§8.1.1) so the controlled peer adopts the same pair; nominate even if that check's
                    // response is missed (the ordinary check already validated the pair).
                    await _check(next.Candidate.EndPoint, true, ct).ConfigureAwait(false);

                    lock (_gate)
                    {
                        _nominatedPriority = next.Candidate.Priority;
                        next.Done = true;
                    }

                    _logger.LogDebug(
                        "ICE nominated remote pair {EndPoint} (priority {Priority}) after a connectivity check " +
                        "(RFC 8445 §7.2.2/§8.1.1).", next.Candidate.EndPoint, next.Candidate.Priority);
                    RaiseNominated(next.Candidate.EndPoint);
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
                            "ICE candidate {EndPoint} did not answer after {Attempts} checks; abandoning it.",
                            next.Candidate.EndPoint, _maxAttempts);

                    // Pace retries and give lower-priority candidates a turn between attempts.
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

    // The highest-priority candidate still worth a check: not finished, attempts remaining, and strictly
    // higher priority than the currently nominated pair (a working higher-priority pair upgrades the
    // selection; nothing at or below the nominated priority is worth checking).
    private IceNominationCandidateState? SelectBestActionable()
    {
        lock (_gate)
        {
            IceNominationCandidateState? best = null;
            foreach (var state in _candidates)
            {
                if (state.Done || state.Attempts >= _maxAttempts || state.Candidate.Priority <= _nominatedPriority)
                    continue;
                if (best is null || state.Candidate.Priority > best.Candidate.Priority)
                    best = state;
            }

            return best;
        }
    }

    private void RaiseNominated(IPEndPoint endPoint)
    {
        try
        {
            _onNominated(endPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in ICE nomination handler.");
        }
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        // Wake on a newly added candidate (signal) or after the round delay to re-check retriable candidates.
        try
        {
            await _signal.WaitAsync(_roundDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
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
