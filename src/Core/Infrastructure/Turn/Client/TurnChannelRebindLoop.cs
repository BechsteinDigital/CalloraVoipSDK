using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Keeps a bound TURN channel alive for the duration of a relayed media session by periodically re-issuing
/// ChannelBind (RFC 8656 §12). A channel binding lapses 10 minutes after it was installed; once media flows as
/// ChannelData over that channel — after a relay pair won ICE and the transport switched to the relay data path —
/// a lapse would silently break the media path. A background loop re-binds at roughly half the channel lifetime,
/// threading the server's rotated credentials into the next bind, early enough that a lost re-bind still has a
/// second attempt before the binding would expire.
/// <para>
/// The re-bind is injected as a delegate (production binds the nominated peer and channel number over the shared
/// media socket), keeping this loop transport-agnostic and deterministically testable — the delay is injectable
/// like <see cref="TurnAllocationRefreshLoop"/>. Unlike an allocation, a channel binding has no explicit teardown
/// — it lapses when no longer re-bound — so disposal only stops the loop.
/// </para>
/// </summary>
internal sealed class TurnChannelRebindLoop : IRelayKeepAlive
{
    // RFC 8656 §12 channel binding lifetime; used to pace the re-bind cadence.
    private const uint DefaultChannelLifetimeSeconds = 600;

    private readonly Func<StunCredentials?, CancellationToken, Task<StunCredentials?>> _rebind;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _retryBackoff;
    private readonly ILogger<TurnChannelRebindLoop> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    // The current long-term credentials, threaded forward as the server rotates the NONCE. Only touched on the
    // loop thread while it runs; disposal reads nothing after awaiting the loop.
    private StunCredentials? _credentials;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Creates the channel rebind loop for an already-bound channel.</summary>
    /// <param name="rebind">
    /// The ChannelBind operation — <c>(credentials, ct)</c> re-binding the same peer and channel number, returning
    /// the server's rotated credentials (or <see langword="null"/> to keep the current ones).
    /// </param>
    /// <param name="initialCredentials">The credentials the initial ChannelBind left primed, threaded into the first re-bind.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="channelLifetimeSeconds">
    /// The channel binding lifetime (RFC 8656 §12 default 600 s) the cadence is paced against; the loop re-binds
    /// at half this value. Non-positive falls back to the RFC default.
    /// </param>
    /// <param name="delay">The delay primitive; injectable for deterministic tests.</param>
    public TurnChannelRebindLoop(
        Func<StunCredentials?, CancellationToken, Task<StunCredentials?>> rebind,
        StunCredentials? initialCredentials,
        ILoggerFactory loggerFactory,
        uint channelLifetimeSeconds = DefaultChannelLifetimeSeconds,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _rebind = rebind ?? throw new ArgumentNullException(nameof(rebind));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _credentials = initialCredentials;
        var lifetime = channelLifetimeSeconds > 0 ? channelLifetimeSeconds : DefaultChannelLifetimeSeconds;
        _interval = TimeSpan.FromSeconds(lifetime / 2.0);
        _delay = delay ?? Task.Delay;
        _retryBackoff = TimeSpan.FromSeconds(5);
        _logger = loggerFactory.CreateLogger<TurnChannelRebindLoop>();
    }

    /// <summary>
    /// Starts the rebind loop on a background task. Idempotent and thread-safe; a second call, or a call after
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(_interval, ct).ConfigureAwait(false);
                _credentials = await _rebind(_credentials, ct).ConfigureAwait(false) ?? _credentials;
                _logger.LogDebug("TURN channel re-bound.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("TURN channel rebind loop stopped.");
                return;
            }
            catch (Exception ex)
            {
                // A single failed re-bind must not abandon the channel: retry after a bounded backoff so a
                // transient error is survived. The binding still has roughly its remaining lifetime; a
                // persistently failing server lets it lapse, which ICE/consent then surface.
                _logger.LogWarning(ex, "TURN channel rebind failed; retrying after {Backoff}.", _retryBackoff);
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

    /// <summary>
    /// Cancels the loop and awaits it. Idempotent. A channel binding has no explicit teardown — it lapses on its
    /// own once no longer re-bound (RFC 8656 §12) — so disposal only stops the loop. Must run before the transport
    /// the re-bind rides is disposed — a composition-layer ordering concern.
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
