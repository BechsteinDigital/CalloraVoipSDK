using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Keeps a TURN allocation alive for the duration of a session by periodically refreshing it (RFC 8656 §3.9):
/// a background loop re-issues a Refresh at roughly half the granted lifetime — early enough that a lost
/// refresh still has a second attempt before the allocation would expire — threading the server's rotated
/// REALM/NONCE and granted lifetime into the next refresh. On disposal it best-effort deletes the allocation
/// with a Refresh of lifetime 0 (RFC 8656 §3.9), so a torn-down session does not leak a server-side allocation
/// until it times out.
/// <para>
/// The refresh transaction is injected as a delegate (production wires it to
/// <see cref="TurnRelayControlClient.RefreshAsync"/> over the shared media socket), keeping this loop
/// transport-agnostic and deterministically testable — the clock/delay are injectable like
/// <c>IceConsentMonitor</c>. This is the keepalive component only; wiring it into a media session's lifecycle
/// (so the loop is disposed — and its teardown sent — before the transport it rides is torn down) is the
/// composition layer's job.
/// </para>
/// </summary>
internal sealed class TurnAllocationRefreshLoop : IAsyncDisposable
{
    // RFC 8656 §3.9 default allocation lifetime; used to pace refreshes when the server did not report a
    // concrete granted lifetime (0).
    private const uint DefaultLifetimeSeconds = 600;

    private readonly Func<StunCredentials?, uint, CancellationToken, Task<TurnRefreshResult>> _refresh;
    private readonly uint _requestedLifetimeSeconds;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _retryBackoff;
    private readonly TimeSpan _teardownTimeout;
    private readonly ILogger<TurnAllocationRefreshLoop> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    // The current long-term credentials and last granted lifetime. Only ever touched on the loop thread while
    // it runs; the teardown reads them after the loop has been awaited to completion (a happens-before barrier),
    // so no additional synchronisation is needed.
    private StunCredentials? _credentials;
    private uint _grantedLifetimeSeconds;
    private bool _allocationGone;

    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Creates the refresh loop for an established allocation.
    /// </summary>
    /// <param name="refresh">
    /// The Refresh transaction — <c>(credentials, lifetimeSeconds, ct)</c> returning the granted lifetime and
    /// rotated credentials. Production binds it to <see cref="TurnRelayControlClient.RefreshAsync"/>.
    /// </param>
    /// <param name="initialCredentials">
    /// The allocation's effective credentials (already realm/nonce-primed), threaded into the first refresh.
    /// </param>
    /// <param name="grantedLifetimeSeconds">The lifetime the allocation was granted, used to pace the first refresh.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="requestedLifetimeSeconds">
    /// The lifetime each refresh requests; defaults to the granted lifetime (or the RFC default when that is 0).
    /// </param>
    /// <param name="delay">The delay primitive; injectable for deterministic tests.</param>
    /// <param name="teardownTimeout">How long the best-effort Refresh-0 teardown is given on disposal.</param>
    public TurnAllocationRefreshLoop(
        Func<StunCredentials?, uint, CancellationToken, Task<TurnRefreshResult>> refresh,
        StunCredentials? initialCredentials,
        uint grantedLifetimeSeconds,
        ILoggerFactory loggerFactory,
        uint? requestedLifetimeSeconds = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? teardownTimeout = null)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _credentials = initialCredentials;
        _grantedLifetimeSeconds = grantedLifetimeSeconds;
        _requestedLifetimeSeconds = requestedLifetimeSeconds
            ?? (grantedLifetimeSeconds > 0 ? grantedLifetimeSeconds : DefaultLifetimeSeconds);
        _delay = delay ?? Task.Delay;
        _teardownTimeout = teardownTimeout ?? TimeSpan.FromSeconds(2);
        _retryBackoff = TimeSpan.FromSeconds(5);
        _logger = loggerFactory.CreateLogger<TurnAllocationRefreshLoop>();
    }

    /// <summary>
    /// Starts the refresh loop on a background task. Idempotent and thread-safe; a second call, or a call
    /// after disposal, is a no-op.
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

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(RefreshDelay(_grantedLifetimeSeconds), ct).ConfigureAwait(false);
                var result = await _refresh(_credentials, _requestedLifetimeSeconds, ct).ConfigureAwait(false);
                _credentials = result.EffectiveCredentials ?? _credentials;
                _grantedLifetimeSeconds = result.LifetimeSeconds;

                if (result.LifetimeSeconds == 0)
                {
                    // The server let the allocation go (or refused to extend it): nothing left to keep alive,
                    // and no teardown is needed. Higher layers observe the dead relay path via ICE/consent.
                    _allocationGone = true;
                    _logger.LogWarning(
                        "TURN allocation refresh returned lifetime 0; the allocation is gone. Stopping refresh.");
                    return;
                }

                _logger.LogDebug(
                    "TURN allocation refreshed; granted lifetime {Lifetime}s.", result.LifetimeSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("TURN allocation refresh loop stopped.");
                return;
            }
            catch (Exception ex)
            {
                // A single failed refresh must not abandon the allocation: retry after a bounded backoff so a
                // transient error (packet loss, brief server hiccup) is survived. The allocation still has
                // roughly its remaining lifetime; a persistently failing server lets it expire, which ICE/consent
                // then surface.
                _logger.LogWarning(ex, "TURN allocation refresh failed; retrying after {Backoff}.", _retryBackoff);
                try
                {
                    await _delay(_retryBackoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    // Refresh at half the granted lifetime (RFC 8656 §3.9 keepalive cadence): a lost refresh then still has a
    // second attempt before the allocation would expire. Falls back to the RFC default lifetime when the server
    // reported none (0).
    private static TimeSpan RefreshDelay(uint grantedLifetimeSeconds) =>
        TimeSpan.FromSeconds((grantedLifetimeSeconds > 0 ? grantedLifetimeSeconds : DefaultLifetimeSeconds) / 2.0);

    /// <summary>
    /// Cancels the loop, awaits it, and best-effort deletes the allocation with a Refresh of lifetime 0
    /// (RFC 8656 §3.9) so it is not left dangling on the server. Idempotent. The teardown is skipped when the
    /// server already dropped the allocation (a refresh returned lifetime 0). Must run before the transport the
    /// refresh rides is disposed — a composition-layer ordering concern.
    /// </summary>
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
        if (loop is not null)
            await loop.ConfigureAwait(false);
        _cts.Dispose();

        if (_allocationGone)
            return;

        // Best-effort delete on a fresh token (the loop's is cancelled). Bounded so a hung teardown cannot
        // block disposal indefinitely; any failure is swallowed — the allocation then simply times out.
        using var teardownCts = new CancellationTokenSource(_teardownTimeout);
        try
        {
            await _refresh(_credentials, 0, teardownCts.Token).ConfigureAwait(false);
            _logger.LogDebug("TURN allocation deleted on teardown (Refresh lifetime 0).");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort TURN allocation teardown (Refresh 0) did not complete.");
        }
    }
}
