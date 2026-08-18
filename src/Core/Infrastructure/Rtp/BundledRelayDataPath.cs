using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Owns a bundle's TURN relay data path (RFC 8656) on the shared 5-tuple: whether a relay ICE local candidate is
/// wired at all, the allocation keepalive (§3.9), the one-shot switch of the transport from the direct path onto
/// ChannelData once a relay pair wins ICE (§11–12), and the teardown order all of that requires.
/// <para>
/// Extracted from <see cref="BundledMediaSession"/>: the session composes transport, DTLS, ICE and tracks, and the
/// relay path is a self-contained lifecycle running across all of them — wired at construction (offerer) or
/// adopted later (answerer), started with the transport, transitioned on a nomination, and drained before the
/// transport it rides is disposed. Keeping it here leaves one place that knows the ordering rules below.
/// </para>
/// <para>
/// Thread-safety (K3): the wiring claim and the transition are one-shot latches (<see cref="Interlocked"/>), and
/// every field crossing a thread boundary — the ICE driver thread nominates, the transition runs on the thread
/// pool, the receive loop reads <see cref="IsActive"/>, and disposal comes from the caller — is read and written
/// through <see cref="Volatile"/>.
/// </para>
/// </summary>
internal sealed class BundledRelayDataPath : IAsyncDisposable
{
    private readonly BundledMediaTransport _transport;
    private readonly ILogger _logger;

    // 0 = no relay candidate wired; 1 = wired (at construction from the options factory, or later via
    // TryAdopt). Guards against wiring the relay path twice (a second indication relay / relay candidate).
    private int _wired;

    // The relay allocation keepalive (RFC 8656 §3.9), when a relay path was wired: started with the session and
    // disposed — running its teardown Refresh(0) — before the transport it rides. Set from the relay binding at
    // construction (offerer) or via TryAdopt (answerer); Volatile for the gather→start/dispose cross-thread read.
    private IRelayKeepAlive? _keepAlive;

    // The relay binding (its ChannelBind seam + relay server), retained so a relay-pair nomination can switch the
    // transport onto the relay data path. Set from the binding at construction (offerer) or TryAdopt (answerer).
    private RelayIceBinding? _binding;

    // The one-shot direct→relay data-path transition, kicked off on the driver thread when a relay pair is
    // nominated. Guarded so it runs at most once; cancelled and awaited before the transport is disposed (its
    // ChannelBind + EnterRelayMode ride the live transport).
    private int _transitionStarted;
    private Task? _transitionTask;
    private readonly CancellationTokenSource _transitionCts = new();

    // Set once the transition actually SUCCEEDED (channel installed) — not merely started, so a failed ChannelBind
    // (transition abandoned, media back on the checked path) still lets a later nomination re-point the transport.
    private int _transitioned;

    // The channel rebind keepalive (RFC 8656 §12), set once the relay data-path transition binds a channel:
    // started right after SetRelayChannel and disposed — before the transport it rides — in DisposeAsync. The
    // channel exists only after the transition, so this starts later than the allocation/permission keepalive.
    // Volatile for the transition-thread write / dispose-thread read.
    private IRelayKeepAlive? _channelRebind;

    /// <param name="transport">The shared bundle transport this relay path rides and switches.</param>
    /// <param name="binding">
    /// The relay binding wired at construction (the offerer, whose TURN allocation was gathered before the session
    /// existed), or <see langword="null"/> when none was — leaving the door open for a later
    /// <see cref="TryAdopt"/>.
    /// </param>
    /// <param name="logger">The owning session's logger.</param>
    public BundledRelayDataPath(BundledMediaTransport transport, RelayIceBinding? binding, ILogger logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // A relay candidate wired at construction (offerer path) closes the door on a later TryAdopt.
        _wired = binding is not null ? 1 : 0;
        // Its keepalive (if any) is started in Start, once the transport's receive loop is up.
        _keepAlive = binding?.KeepAlive;
        // Retained so a relay-pair nomination can switch the transport onto the relay data path.
        _binding = binding;
    }

    /// <summary>
    /// Whether the transport has switched onto the relay data path (RFC 8656 ChannelData). Once true the transport
    /// is relay-committed to the bound peer: a later relay→direct re-nomination must not re-point its remote (the
    /// bound channel forwards to the relay peer; re-pointing would mis-attribute inbound ChannelData).
    /// </summary>
    public bool IsActive => Volatile.Read(ref _transitioned) != 0;

    /// <summary>
    /// Adopts a relay ICE local candidate after the session was already built — the answerer path, whose TURN
    /// allocation only finished gathering post-construction. Invokes <paramref name="relayIceBindingFactory"/> with
    /// the transport's unframed send to build the relay wiring, routes inbound relayed Data indications and the
    /// relay server's control responses into the transport
    /// (<see cref="BundledMediaTransport.SetIndicationRelay"/>), retains the binding for a later nomination, and
    /// starts its allocation keepalive.
    /// <para>
    /// Returns the adopted binding so the caller can hand its send path and permission installer to the ICE agent —
    /// the one part of the adoption that belongs to ICE, not to the transport. Idempotent: returns
    /// <see langword="null"/> once the relay path is already wired (at construction or a prior adoption) or when the
    /// factory yields no binding, and the claim is released in the latter case so a later adoption can still wire it.
    /// </para>
    /// </summary>
    /// <param name="relayIceBindingFactory">Builds the relay binding from the transport's unframed send.</param>
    /// <returns>The adopted binding, or <see langword="null"/> when nothing was adopted.</returns>
    public RelayIceBinding? TryAdopt(RelayIceBindingFactory relayIceBindingFactory)
    {
        ArgumentNullException.ThrowIfNull(relayIceBindingFactory);
        if (Interlocked.Exchange(ref _wired, 1) != 0)
            return null;

        var binding = relayIceBindingFactory.Invoke(_transport.SendUnframedAsync);
        if (binding is null)
        {
            // No allocation after all — release the claim so a later adoption can still wire the relay path.
            Volatile.Write(ref _wired, 0);
            return null;
        }

        _transport.SetIndicationRelay(binding.Indication, binding.OnControl);
        // Retain the binding so a later relay-pair nomination can ChannelBind + switch the transport.
        Volatile.Write(ref _binding, binding);

        // Keep the adopted allocation alive. Started here (idempotent) so an adoption that lands after the session
        // started still runs the keepalive; the Start below covers the pre-start case. Starting before the transport
        // receive loop is up is safe — the first refresh is roughly half the allocation lifetime away.
        Volatile.Write(ref _keepAlive, binding.KeepAlive);
        binding.KeepAlive?.Start();
        return binding;
    }

    /// <summary>
    /// Keeps a gathered relay allocation alive for the session (RFC 8656 §3.9). Called once the transport's receive
    /// loop is up; idempotent — <see cref="TryAdopt"/> may already have started it for an answerer.
    /// </summary>
    public void Start() => Volatile.Read(ref _keepAlive)?.Start();

    /// <summary>
    /// A relay pair won ICE: switch the transport onto the relay data path (RFC 8656). Runs on the driver thread
    /// right after the session has already pointed the transport's remote and DTLS at the peer (the precondition
    /// <see cref="BundledMediaTransport.EnterRelayMode"/> needs), so it only kicks off the async transition — at
    /// most once — and returns.
    /// </summary>
    /// <param name="peer">The nominated relay pair's remote endpoint.</param>
    public void OnRelayPairNominated(IPEndPoint peer)
    {
        if (Interlocked.Exchange(ref _transitionStarted, 1) != 0)
            return;
        Volatile.Write(ref _transitionTask, Task.Run(() => TransitionAsync(peer)));
    }

    // ChannelBind the peer while the transport is still in direct mode (the request reaches the server unframed
    // via the relay control stack), then flip the transport into relay mode and install the bound channel — media
    // then flows as ChannelData through the TURN server (RFC 8656 §11–12). A failed ChannelBind leaves media on
    // the checked path (logged); a disposing session cancels it.
    private async Task TransitionAsync(IPEndPoint peer)
    {
        var binding = Volatile.Read(ref _binding);
        if (binding?.BindChannel is not { } bindChannel)
            return;

        try
        {
            var channelBinding = await bindChannel(peer, _transitionCts.Token).ConfigureAwait(false);
            // Re-assert the relay peer as the transport remote right before the flip, in case a direct
            // re-nomination re-pointed it during the (sub-second) ChannelBind — the bound channel forwards to
            // this peer, and inbound ChannelData is attributed to it.
            _transport.SetRemoteEndPoint(peer);
            _transport.EnterRelayMode(binding.Indication.RelayServer, binding.OnControl);
            _transport.SetRelayChannel(channelBinding.Channel);
            // Commit: from here a later re-nomination must not re-point the transport (see IsActive).
            Volatile.Write(ref _transitioned, 1);
            // Keep the channel binding alive (RFC 8656 §12): start the rebind loop now — the channel exists only
            // after this transition — and dispose it before the transport it rides (DisposeAsync).
            if (channelBinding.Rebind is { } channelRebind)
            {
                Volatile.Write(ref _channelRebind, channelRebind);
                channelRebind.Start();
            }
            _logger.LogInformation(
                "Relay data path activated for the nominated relay pair: media now flows as ChannelData through the " +
                "TURN server (RFC 8656 §11–12).");
        }
        catch (OperationCanceledException) when (_transitionCts.IsCancellationRequested)
        {
            // Session disposing — abort the transition.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to switch onto the relay data path after nominating a relay pair; media stays on the checked path.");
        }
    }

    /// <summary>
    /// Tears the relay path down in the order its pieces ride each other. The caller MUST have disposed the ICE
    /// agent first (so no new nomination starts a transition) and MUST NOT yet have disposed the transport (the
    /// teardown Refresh(0) rides its control send).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Drain a relay data-path transition in flight before the transport it rides is disposed: the driver is
        // now stopped (no new transition starts), so cancel and await the running one.
        await _transitionCts.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref _transitionTask) is { } transition)
            await transition.ConfigureAwait(false);
        _transitionCts.Dispose();
        // Dispose the channel rebind loop (RFC 8656 §12) before the allocation keepalive: both ride the
        // transport's control send, and the rebind stops first so it does not re-bind a channel the allocation
        // teardown is about to drop.
        if (Volatile.Read(ref _channelRebind) is { } channelRebind)
            await channelRebind.DisposeAsync().ConfigureAwait(false);
        // The allocation keepalive last: its teardown Refresh(0) rides the transport's control send, so the
        // transport must still be alive to carry it.
        if (Volatile.Read(ref _keepAlive) is { } keepAlive)
            await keepAlive.DisposeAsync().ConfigureAwait(false);
    }
}
