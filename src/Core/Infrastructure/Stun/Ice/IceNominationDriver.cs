using System.Net;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Drives the controlling agent's ICE candidate-pair connectivity checks and nomination over the shared
/// media socket (RFC 8445 §7.2.2 checks, §8.1.1 regular nomination). It visits the remote candidates in
/// descending priority order and, for each, sends an ordinary connectivity check through the injected
/// <paramref name="check"/> delegate (backed by the receive-loop-matched
/// <see cref="IceMediaConsentSession.SendCheckAsync"/>); the first candidate that answers is a valid pair,
/// so — because candidates are visited highest-priority first — it is the highest-priority valid pair. The
/// driver then sends a nominating check carrying USE-CANDIDATE and reports the selected pair through
/// <paramref name="onNominated"/> so the caller can redirect the media send target and consent freshness to
/// it. If no candidate answers within the bounded rounds it stops without nominating, leaving the initial
/// remote in place (the symmetric transport still latches the peer's real source on the first packet).
/// <para>
/// Receive-loop integration is what makes this non-facade: the checks and their responses share the one
/// media socket the transport owns after start, matched by transaction id, while the peer's inbound handler
/// answers our checks — so it works while both agents are up (no pre-start bootstrapping deadlock). Only the
/// controlling agent runs a driver; the controlled agent adopts the nominated pair from the peer's
/// USE-CANDIDATE check (RFC 8445 §7.3.1.5).
/// </para>
/// </summary>
internal sealed class IceNominationDriver : IAsyncDisposable
{
    private readonly IReadOnlyList<IceRemoteCandidate> _candidates;
    private readonly Func<IPEndPoint, bool, CancellationToken, Task<bool>> _check;
    private readonly Action<IPEndPoint> _onNominated;
    private readonly int _maxRounds;
    private readonly TimeSpan _roundDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<IceNominationDriver> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Creates the nomination driver.
    /// </summary>
    /// <param name="candidates">The remote candidates to check, in any order (visited highest-priority first).</param>
    /// <param name="check">
    /// Sends one connectivity check to a target — <c>(target, useCandidate, ct)</c> — and returns
    /// <see langword="true"/> when a matching response arrives. Wired to the shared consent session so the
    /// check and its response ride the media socket's receive loop.
    /// </param>
    /// <param name="onNominated">Invoked once with the nominated remote endpoint when a pair is confirmed.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="maxRounds">
    /// How many times to re-sweep the candidates before giving up — a candidate may not answer immediately
    /// while the peer's transport is still coming up.
    /// </param>
    /// <param name="roundDelay">Delay between sweeps; injectable for deterministic tests.</param>
    /// <param name="delay">The delay primitive; injectable for deterministic tests (defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).</param>
    public IceNominationDriver(
        IReadOnlyList<IceRemoteCandidate> candidates,
        Func<IPEndPoint, bool, CancellationToken, Task<bool>> check,
        Action<IPEndPoint> onNominated,
        ILoggerFactory loggerFactory,
        int maxRounds = 10,
        TimeSpan? roundDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _onNominated = onNominated ?? throw new ArgumentNullException(nameof(onNominated));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _maxRounds = maxRounds > 0 ? maxRounds : throw new ArgumentOutOfRangeException(nameof(maxRounds));
        _roundDelay = roundDelay ?? TimeSpan.FromMilliseconds(200);
        _delay = delay ?? Task.Delay;
        _logger = loggerFactory.CreateLogger<IceNominationDriver>();
    }

    /// <summary>
    /// Starts the check/nomination loop on a background task. Idempotent and thread-safe; a second call, a
    /// call after disposal, or an empty candidate set is a no-op.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed || _candidates.Count == 0)
                return;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var ordered = _candidates.OrderByDescending(c => c.Priority).ToArray();

        for (var round = 0; round < _maxRounds && !ct.IsCancellationRequested; round++)
        {
            foreach (var candidate in ordered)
            {
                bool answered;
                try
                {
                    answered = await _check(candidate.EndPoint, false, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                if (!answered)
                    continue;

                // Highest-priority valid pair (visited priority-first). Send a nominating check carrying
                // USE-CANDIDATE (RFC 8445 §8.1.1) so the controlled peer adopts the same pair, then report
                // it. The pair is already validated by the ordinary check above, so nominate even if the
                // nominating check's response is missed.
                try
                {
                    await _check(candidate.EndPoint, true, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogDebug(
                    "ICE nominated remote pair {EndPoint} after a connectivity check (RFC 8445 §7.2.2/§8.1.1).",
                    candidate.EndPoint);
                try
                {
                    _onNominated(candidate.EndPoint);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception in ICE nomination handler.");
                }

                return;
            }

            try
            {
                await _delay(_roundDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogWarning(
            "ICE connectivity checks confirmed no candidate pair after {Rounds} rounds; keeping the initial remote " +
            "(the symmetric transport still latches the peer's source).", _maxRounds);
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
    }
}
