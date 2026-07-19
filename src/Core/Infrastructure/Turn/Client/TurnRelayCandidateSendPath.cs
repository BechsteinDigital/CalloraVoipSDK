using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// The outbound send path of a relay ICE local candidate (RFC 8445 §5.1.1.2): it frames a datagram — an ICE
/// connectivity check during the checking phase — as a TURN Send indication (RFC 8656 §10) addressed to a
/// specific remote candidate, after ensuring the relay has a permission for that peer's IP (RFC 8656 §9), and
/// sends the framed datagram to the relay server through an injected raw-send delegate (the media transport's
/// targeted send). Its <see cref="SendAsync"/> matches the send-path delegate shape the consent session and
/// nomination driver drive, so a relay candidate plugs in exactly like the direct one.
/// <para>
/// A permission is installed once per peer IP and cached: connectivity checks retransmit, so re-issuing
/// CreatePermission on every check would flood the control plane; the checking phase is short enough that
/// permission refresh (RFC 8656 §9, ~5 min lifetime) is a later concern. A permission that fails is dropped
/// from the cache so the next check retransmit re-attempts it rather than poisoning the peer permanently. The
/// long-term credentials rotate with the server's NONCE across control operations and are carried forward.
/// </para>
/// <para>
/// It performs no socket I/O of its own and holds no transport type, so it composes above any media transport
/// through the injected raw-send delegate. Wiring it to a concrete transport (the CreatePermission control
/// round-trips ride the media socket too) is the caller's job.
/// </para>
/// </summary>
internal sealed class TurnRelayCandidateSendPath
{
    private readonly IRelayIndicationChannel _indication;
    private readonly TurnRelayControlClient _control;
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> _rawSend;
    private readonly ILogger<TurnRelayCandidateSendPath> _logger;

    // Permission requests deduplicated per peer IP (Lazy so the CreatePermission fires exactly once under
    // concurrent checks to the same peer). A faulted entry is removed by CreatePermissionAsync so it retries.
    private readonly ConcurrentDictionary<IPAddress, Lazy<Task>> _permissions = new();

    // Serializes permission installs so the credentials' server-rotated NONCE is threaded atomically across
    // peers (read → CreatePermission → write, no lost update). Not disposed: AvailableWaitHandle is never
    // accessed, so no wait handle is allocated; WaitAsync/Release publish _credentials with happens-before.
    private readonly SemaphoreSlim _permissionGate = new(1, 1);
    private StunCredentials? _credentials;

    /// <summary>Creates the relay candidate send path over the allocation's control client and credentials.</summary>
    /// <param name="indication">The indication channel that frames Send indications for the allocation's relay server.</param>
    /// <param name="control">The authenticated control client used to install per-peer permissions.</param>
    /// <param name="credentials">
    /// The allocation's effective credentials (already REALM/NONCE-primed by the Allocate flow), or
    /// <see langword="null"/> for an open server. Carried forward as the server rotates the NONCE.
    /// </param>
    /// <param name="rawSend">
    /// Sends a framed datagram to a target over the media socket — <c>(datagram, target, ct)</c>, typically the
    /// transport's targeted send. Send indications are sent to <see cref="IRelayIndicationChannel.RelayServer"/>.
    /// </param>
    /// <param name="logger">Logger.</param>
    public TurnRelayCandidateSendPath(
        IRelayIndicationChannel indication,
        TurnRelayControlClient control,
        StunCredentials? credentials,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> rawSend,
        ILogger<TurnRelayCandidateSendPath> logger)
    {
        _indication = indication ?? throw new ArgumentNullException(nameof(indication));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _rawSend = rawSend ?? throw new ArgumentNullException(nameof(rawSend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _credentials = credentials;
    }

    /// <summary>
    /// Sends <paramref name="datagram"/> to <paramref name="remoteTarget"/> through the relay: it ensures a
    /// permission for the peer's IP exists, frames the datagram as a Send indication addressed to the peer, and
    /// sends it to the relay server. Matches the relay-candidate send-path delegate
    /// <c>(datagram, remoteTarget, ct)</c>. Throws when the permission cannot be installed or the raw send
    /// fails, so the ICE check counts as unanswered and is retried (the caller treats a throw as a failed send).
    /// </summary>
    /// <param name="datagram">The datagram to relay (an ICE connectivity check).</param>
    /// <param name="remoteTarget">The remote candidate the relay should forward the datagram to.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteTarget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remoteTarget);

        await EnsurePermissionAsync(remoteTarget.Address, ct).ConfigureAwait(false);

        var framed = _indication.Wrap(remoteTarget, datagram.Span);
        await _rawSend(framed, _indication.RelayServer, ct).ConfigureAwait(false);
    }

    // The permission task is shared across concurrent checks to the same peer, so it runs under
    // CancellationToken.None (self-bounded by the transactor's RTO schedule) — a single caller's cancellation
    // must not cancel a permission others depend on. Each caller instead observes its own token via WaitAsync,
    // bailing out of the wait without disturbing the shared install.
    private Task EnsurePermissionAsync(IPAddress peerAddress, CancellationToken ct)
        => _permissions.GetOrAdd(peerAddress, ip => new Lazy<Task>(() => CreatePermissionAsync(ip))).Value.WaitAsync(ct);

    private async Task CreatePermissionAsync(IPAddress peerAddress)
    {
        // Hold the gate across the whole read-call-write so two peers' installs cannot interleave and lose the
        // NONCE update. Under the ICE checking phase the peer set is small, so serial installs are cheap.
        await _permissionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A permission is keyed by IP (RFC 8656 §9); the port is ignored, so 0 stands in.
            var effective = await _control
                .CreatePermissionAsync(new IPEndPoint(peerAddress, 0), _credentials, CancellationToken.None)
                .ConfigureAwait(false);

            if (effective is not null)
                _credentials = effective;
        }
        catch (Exception ex)
        {
            // Drop the cache entry so the next check retransmit re-attempts the permission rather than being
            // stuck on this failure forever; rethrow so the send fails and the ICE check is retried.
            _permissions.TryRemove(peerAddress, out _);
            _logger.LogDebug(ex, "TURN CreatePermission for peer {Peer} failed; will retry on the next check.", peerAddress);
            throw;
        }
        finally
        {
            _permissionGate.Release();
        }
    }
}
