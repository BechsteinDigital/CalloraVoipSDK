using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Keeps a relay's per-peer permissions alive for the duration of a session by periodically re-issuing
/// CreatePermission (RFC 8656 §9). A permission lapses 5 minutes after it was installed, so once media — or a
/// long checking phase — outlives that window the relay silently discards inbound datagrams from the peer. A
/// background loop refreshes at roughly half the permission lifetime, early enough that a lost refresh still
/// has a second attempt before the permission would expire.
/// <para>
/// The refresh is injected as a delegate (production wires it to
/// <see cref="TurnRelayCandidateSendPath.RefreshInstalledPermissionsAsync"/>, which re-installs every known
/// peer under the credential gate), keeping this loop transport-agnostic and deterministically testable — the
/// delay is injectable like <see cref="TurnAllocationRefreshLoop"/>. Unlike an allocation, a permission has no
/// explicit teardown — it simply lapses when it is no longer refreshed — so disposal only stops the loop.
/// </para>
/// </summary>
internal sealed class TurnPermissionRefreshLoop : IRelayKeepAlive
{
    // RFC 8656 §9 permission lifetime; used to pace the refresh cadence.
    private const uint DefaultPermissionLifetimeSeconds = 300;

    private readonly Func<CancellationToken, Task<bool>> _refresh;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _retryBackoff;
    private readonly ILogger<TurnPermissionRefreshLoop> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>Creates the permission refresh loop.</summary>
    /// <param name="refresh">
    /// The refresh operation — re-installs all currently known peer permissions and returns whether every peer
    /// succeeded (<see langword="false"/> shortens the next wait to the backoff so a failed peer is re-attempted
    /// well inside its lifetime). Production binds it to
    /// <see cref="TurnRelayCandidateSendPath.RefreshInstalledPermissionsAsync"/>.
    /// </param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="permissionLifetimeSeconds">
    /// The permission lifetime the refresh cadence is paced against (RFC 8656 §9 default 300 s); the loop
    /// refreshes at half this value. Non-positive falls back to the RFC default.
    /// </param>
    /// <param name="delay">The delay primitive; injectable for deterministic tests.</param>
    public TurnPermissionRefreshLoop(
        Func<CancellationToken, Task<bool>> refresh,
        ILoggerFactory loggerFactory,
        uint permissionLifetimeSeconds = DefaultPermissionLifetimeSeconds,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var lifetime = permissionLifetimeSeconds > 0 ? permissionLifetimeSeconds : DefaultPermissionLifetimeSeconds;
        _interval = TimeSpan.FromSeconds(lifetime / 2.0);
        _delay = delay ?? Task.Delay;
        _retryBackoff = TimeSpan.FromSeconds(5);
        _logger = loggerFactory.CreateLogger<TurnPermissionRefreshLoop>();
    }

    /// <summary>
    /// Starts the refresh loop on a background task. Idempotent and thread-safe; a second call, or a call after
    /// disposal, is a no-op.
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
        // The next wait: the full interval after a fully successful refresh, the short backoff after a failure
        // (a thrown error OR a partial per-peer failure signalled by a false result). Retrying after only the
        // backoff keeps the second attempt inside the permission lifetime — waiting a full interval again would
        // push it past the ~5-minute expiry, defeating the lifetime/2 cadence.
        var nextDelay = _interval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(nextDelay, ct).ConfigureAwait(false);
                if (await _refresh(ct).ConfigureAwait(false))
                {
                    nextDelay = _interval;
                    _logger.LogDebug("TURN permissions refreshed.");
                }
                else
                {
                    nextDelay = _retryBackoff;
                    _logger.LogWarning(
                        "At least one TURN permission refresh failed; retrying after {Backoff}.", _retryBackoff);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("TURN permission refresh loop stopped.");
                return;
            }
            catch (Exception ex)
            {
                // A single failed refresh must not abandon the permissions: back off briefly and retry (the
                // permissions still have roughly their remaining lifetime); a persistently failing server lets
                // them lapse, which ICE/consent then surface.
                nextDelay = _retryBackoff;
                _logger.LogWarning(ex, "TURN permission refresh failed; retrying after {Backoff}.", _retryBackoff);
            }
        }
    }

    /// <summary>
    /// Cancels the loop and awaits it. Idempotent. There is no permission teardown — a permission lapses on its
    /// own once no longer refreshed (RFC 8656 §9) — so disposal only stops the loop. Must run before the
    /// transport the refresh rides is disposed — a composition-layer ordering concern.
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
    }
}
