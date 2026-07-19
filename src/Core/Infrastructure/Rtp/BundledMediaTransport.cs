using System.Buffers;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The shared 5-tuple of a BUNDLE group (ADR-011 B3, RFC 8843): one UDP socket that carries STUN, DTLS,
/// RTCP, and every RTP stream for all bundled m-lines. It resolves the socket-loop decision the earlier
/// slices deferred — the bundle runs its own focused receive loop rather than reshaping the single-stream
/// <see cref="Session.RtpSession"/>. Inbound datagrams are pushed into the <see cref="BundledInboundPipeline"/>
/// (B2c-in-3), and the transport is itself the <see cref="IBundledDatagramSender"/> the
/// <see cref="BundledOutboundPipeline"/> (B2c-in-4) sends through, so one socket drives both directions.
///
/// This slice owns the socket and the loop only; the shared DTLS association (B3-2) and ICE agent plus
/// consent loop (B3-3) attach on top through the pipelines' STUN/DTLS events and key installation. The
/// receive loop is single-threaded and every retained byte is copied downstream (the pipeline copies
/// STUN/DTLS, SRTP returns fresh arrays), so one pooled buffer is reused across datagrams.
///
/// In TURN relay mode (RFC 8656) the transport runs in two phases on the same 5-tuple. A relay server
/// address (<see cref="BundledMediaTransportOptions.RelayServer"/>) enables the <em>control phase</em>:
/// <see cref="SendControlAsync"/> sends TURN requests to the server and control responses from it are
/// surfaced via <see cref="BundledMediaTransportOptions.OnRelayControl"/>, so the allocation can be
/// established before any channel exists (outbound media is suppressed meanwhile). Once the channel-bind
/// completes, <see cref="SetRelayChannel"/> installs the <see cref="IRelayDatagramChannel"/> and the
/// <em>data phase</em> begins: every outbound datagram is framed as ChannelData to the relay server and
/// every inbound one is unwrapped from it, below the packet demux — so STUN checks, DTLS flights and
/// RTP/RTCP all traverse the one bound channel uniformly and the DTLS/ICE layers above compose unchanged.
/// A channel already known up front may instead be supplied via <see cref="BundledMediaTransportOptions.Relay"/>.
/// </summary>
internal sealed class BundledMediaTransport : IBundledDatagramSender, IRelayControlTransport, IAsyncDisposable
{
    private const int ReceiveBufferSize = 8192;

    private readonly BundledInboundPipeline _inbound;
    private readonly ILogger<BundledMediaTransport> _logger;
    private readonly IPEndPoint _localEndPoint;
    // The whole-socket relay mode discriminator (RFC 8656 §11–12): non-null routes every send/receive through the
    // TURN server. Set at construction for a relay-mode transport, or once at runtime via EnterRelayMode when a
    // relay ICE pair is nominated on a formerly-direct transport. Mutable → read via Volatile on every path (the
    // receive loop reads it concurrently with the nomination thread that flips it).
    private IPEndPoint? _relayServer;
    private Action<ReadOnlyMemory<byte>>? _onRelayControl;
    private readonly UdpClient _udp;

    private IRelayDatagramChannel? _relay;
    // The per-pair relay indication path (RFC 8656 §10), active in direct mode only: relayed Data indications
    // from its server are unwrapped to the inner payload + peer, and the server's control responses go to the
    // control callback. Independent of the whole-socket relay mode above (_relayServer) — mutually exclusive.
    // Read via Volatile from the receive loop.
    private IRelayIndicationChannel? _indicationRelay;
    private Action<ReadOnlyMemory<byte>>? _onIndicationControl;
    private IPEndPoint? _remoteEndPoint;
    private Task? _receiveLoop;
    private CancellationTokenSource? _loopCts;
    private int _started;

    public BundledMediaTransport(
        BundledMediaTransportOptions options,
        BundledInboundPipeline inbound,
        ILogger<BundledMediaTransport> logger,
        UdpClient? socket = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.LocalEndPoint);
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
        _localEndPoint  = options.LocalEndPoint;
        _remoteEndPoint = options.RemoteEndPoint;
        _relay          = options.Relay;
        _onRelayControl = options.OnRelayControl;

        // Relay mode is keyed by the server address, taken from RelayServer or (for a channel supplied up
        // front) derived from it. When both are given they must agree.
        _relayServer = options.RelayServer ?? options.Relay?.RelayServer;
        if (options.RelayServer is not null && options.Relay is not null
            && !RelayEndPoint.SameEndPoint(options.RelayServer, options.Relay.RelayServer))
        {
            throw new ArgumentException(
                "RelayServer and Relay.RelayServer must refer to the same TURN server.", nameof(options));
        }

        // A relay channel is bound to one peer (RFC 8656 channel-bind is per-peer), so the peer is known
        // whenever relay mode is active. Require it: relayed inbound datagrams physically arrive from the
        // TURN server but must be attributed to that peer (not the server) for the DTLS/ICE source filters
        // above, and there is no correct peer to fabricate otherwise.
        if (_relayServer is not null && _remoteEndPoint is null)
            throw new ArgumentException(
                "A relay transport requires a RemoteEndPoint (the bound peer the relay forwards to).", nameof(options));

        if (socket is not null)
        {
            // Reuse a socket the peer bound early (Trickle-ICE early-bind), so the offer could advertise
            // the real ephemeral port before this session existed. The transport owns it from here on and
            // disposes it like a self-bound socket.
            _udp = socket;
        }
        else
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.ReceiveBufferSize = ReceiveBufferSize;
            _udp.Client.Bind(_localEndPoint);
        }
    }

    /// <summary>The endpoint the shared socket is bound to (the actual port after an ephemeral bind).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>The remote endpoint outbound datagrams are sent to, or null before one is set.</summary>
    public IPEndPoint? RemoteEndPoint => Volatile.Read(ref _remoteEndPoint);

    /// <summary>
    /// Points sends at a (new) remote endpoint — the ICE-nominated pair, or the peer address once known.
    /// Read atomically by the send path.
    /// </summary>
    public void SetRemoteEndPoint(IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        Volatile.Write(ref _remoteEndPoint, remoteEndPoint);
    }

    /// <summary>
    /// Installs the bound relay channel once the allocation's channel-bind completes, switching the data
    /// path from suppressed to ChannelData-framed. Valid only in relay mode (a relay server was configured),
    /// and the channel must belong to that same server. Read atomically by the send and receive paths.
    /// </summary>
    /// <param name="channel">The channel produced by the allocation sequence.</param>
    /// <exception cref="InvalidOperationException">The transport is not in relay mode.</exception>
    /// <exception cref="ArgumentException"><paramref name="channel"/> is bound to a different server.</exception>
    public void SetRelayChannel(IRelayDatagramChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (Volatile.Read(ref _relayServer) is not { } relayServer)
            throw new InvalidOperationException("SetRelayChannel is only valid on a relay-mode transport.");
        if (!RelayEndPoint.SameEndPoint(channel.RelayServer, relayServer))
            throw new ArgumentException("The relay channel is bound to a different TURN server.", nameof(channel));

        Volatile.Write(ref _relay, channel);
    }

    /// <summary>
    /// Transitions a <em>direct-mode</em> transport into whole-socket relay mode at runtime (RFC 8656 §11–12),
    /// once a relay ICE pair has been nominated: subsequent sends are suppressed until <see cref="SetRelayChannel"/>
    /// binds a channel, then every datagram (STUN/DTLS/RTP/RTCP) is framed as ChannelData to
    /// <paramref name="relayServer"/> and inbound datagrams are unwrapped from it — the peer is reached through the
    /// relay, not directly. The nominated peer must already be the transport's remote endpoint (relayed inbound is
    /// attributed to it). The per-pair indication path used during checking (<see cref="SetIndicationRelay"/>) goes
    /// dormant — the relay-mode receive branch supersedes it — so its channel is left untouched. Call at most once
    /// and only on a transport not already in relay mode; the control sink is published before the mode flips so a
    /// control response arriving the instant the mode changes still has a sink.
    /// </summary>
    /// <param name="relayServer">The TURN server the allocation lives on.</param>
    /// <param name="onControl">Sink for the relay server's non-ChannelData STUN control responses (ChannelBind/Refresh).</param>
    /// <exception cref="InvalidOperationException">Already in relay mode, or no remote endpoint (bound peer) is set.</exception>
    public void EnterRelayMode(IPEndPoint relayServer, Action<ReadOnlyMemory<byte>>? onControl)
    {
        ArgumentNullException.ThrowIfNull(relayServer);
        if (Volatile.Read(ref _relayServer) is not null)
            throw new InvalidOperationException("The transport is already in relay mode.");
        if (Volatile.Read(ref _remoteEndPoint) is null)
            throw new InvalidOperationException(
                "EnterRelayMode requires a remote endpoint — the bound peer relayed inbound traffic is attributed to.");

        // Publish the control sink before flipping the mode discriminator (the receive loop's gate), so observing
        // relay mode implies the sink is visible too.
        Volatile.Write(ref _onRelayControl, onControl);
        Volatile.Write(ref _relayServer, relayServer);
    }

    /// <summary>
    /// Enables the per-pair TURN relay indication path (RFC 8656 §10) on a <em>direct-mode</em> transport, so a
    /// relay ICE local candidate can send connectivity checks to several remote candidates over one allocation
    /// (framed as Send indications) while the direct host/srflx candidate keeps using the socket directly.
    /// Inbound: Data indications from <paramref name="indicationRelay"/>'s server are unwrapped to the inner
    /// payload and the peer they were relayed from (presented as the source to the pipeline); other STUN from
    /// the server (CreatePermission/Refresh responses) is handed to <paramref name="onControl"/> to match by
    /// transaction id; relay-server traffic never reaches the media pipeline as a direct peer. Typically
    /// called before <see cref="StartAsync"/> (the allocation is adopted before the loop runs), but safe to
    /// call after: both fields are published under Volatile and read atomically by the receive loop.
    /// </summary>
    /// <param name="indicationRelay">The indication channel (relay server + framing).</param>
    /// <param name="onControl">Sink for the relay server's non-Data STUN control responses, or null to drop them.</param>
    /// <exception cref="InvalidOperationException">The transport runs the whole-socket relay mode (mutually exclusive).</exception>
    public void SetIndicationRelay(
        IRelayIndicationChannel indicationRelay,
        Action<ReadOnlyMemory<byte>>? onControl = null)
    {
        ArgumentNullException.ThrowIfNull(indicationRelay);
        if (Volatile.Read(ref _relayServer) is not null)
            throw new InvalidOperationException(
                "SetIndicationRelay is for direct-mode transports; this transport runs the whole-socket relay mode.");

        // Publish the control callback before the channel (the receive loop's gate), so observing the channel
        // implies the callback is visible too.
        Volatile.Write(ref _onIndicationControl, onControl);
        Volatile.Write(ref _indicationRelay, indicationRelay);
    }

    /// <summary>Starts the shared receive loop. Idempotent per instance; call once after wiring.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Claim the start atomically (HARD-C5): a second call must not replace _loopCts/_receiveLoop,
        // which would orphan the first CTS (never disposed) and leave the first loop running.
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return Task.CompletedTask;

        // Link the caller token so DisposeAsync can stop the loop by cancellation before the socket is
        // disposed — cancelling the pending receive yields a clean OperationCanceledException, whereas
        // disposing the socket underneath a pending receive can surface as a raw fault.
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = RunReceiveLoopAsync(_loopCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Test seam (HARD-C5): the current receive-loop task, to assert repeated starts do not replace it.</summary>
    internal Task? ReceiveLoopForTest => _receiveLoop;

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _relay) is { } relay)
        {
            // Relay mode: frame as ChannelData and send to the TURN server, which forwards it to the bound
            // peer (RFC 8656 §11–12). The peer is reached through the relay, not the (suppressed) direct remote.
            var framed = relay.Wrap(datagram.Span);
            await _udp.SendAsync(framed, relay.RelayServer, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Volatile.Read(ref _relayServer) is not null)
        {
            // Relay mode, but the channel is not bound yet (allocation still in progress) — suppress media
            // rather than send it unframed to the relay server, which would drop it. Trace-level: this can
            // fire per ICE retransmit during the (brief) allocation window.
            _logger.LogTrace("Suppressing outbound datagram on {LocalEndPoint}: relay channel not bound yet.", _localEndPoint);
            return;
        }

        if (Volatile.Read(ref _remoteEndPoint) is not { } remote)
        {
            // No peer address yet (pre-ICE) — suppress rather than send to nowhere.
            _logger.LogDebug("Suppressing outbound datagram on {LocalEndPoint}: no remote endpoint set yet.", _localEndPoint);
            return;
        }

        await _udp.SendAsync(datagram, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a datagram to an explicit target rather than the default remote. ICE needs this: a STUN
    /// response goes back to the source of the check, and a triggered check to a peer-reflexive address
    /// (RFC 8445 §7.3), neither of which is necessarily the nominated remote.
    /// </summary>
    public async ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (Volatile.Read(ref _relay) is { } relay)
        {
            // In relay mode the peer is reachable only through the TURN server, so an ICE response / triggered
            // check to the peer is framed as ChannelData to the relay too. `target` is intentionally not sent
            // to directly: the one bound channel carries every send to the bound peer (a different candidate
            // of the same peer still arrives via the relay). Reaching a genuinely different peer would need a
            // second allocation/channel — out of scope for the single-channel relay.
            var framed = relay.Wrap(datagram.Span);
            await _udp.SendAsync(framed, relay.RelayServer, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Volatile.Read(ref _relayServer) is not null)
        {
            // Relay mode, channel not bound yet — an ICE check to the peer cannot go out unframed. Suppress
            // it; ICE will retransmit once the channel is bound. Trace-level: fires per ICE retransmit.
            _logger.LogTrace("Suppressing targeted datagram on {LocalEndPoint}: relay channel not bound yet.", _localEndPoint);
            return;
        }

        await _udp.SendAsync(datagram, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a TURN control request (Allocate / CreatePermission / ChannelBind / Refresh) to the relay
    /// server on the shared socket, <em>unwrapped</em> — it is addressed to the server itself, not framed as
    /// data for the peer. Its response arrives on the receive loop and is surfaced via the relay-control
    /// callback. Only valid in relay mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transport is not in relay mode.</exception>
    public async ValueTask SendControlAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _relayServer) is not { } relayServer)
            throw new InvalidOperationException("SendControlAsync is only valid on a relay-mode transport.");

        await _udp.SendAsync(request, relayServer, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Bundled media receive loop started on {LocalEndPoint}", _localEndPoint);

        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var remoteTemplate = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.Client
                        .ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, cancellationToken)
                        .ConfigureAwait(false);
                    DeliverInbound(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break; // Socket disposed during shutdown.
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    // Torn down during shutdown — a pending receive can surface the socket close as a
                    // WSAECONNRESET after a prior send hit an ICMP port-unreachable. Benign; stop.
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "Bundled media socket error on {LocalEndPoint}", _localEndPoint);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _logger.LogDebug("Bundled media receive loop stopped on {LocalEndPoint}", _localEndPoint);
    }

    // Hands one received datagram to the inbound pipeline. In relay mode only datagrams relayed back through
    // the bound channel are ours: unwrap to the inner payload and present the relayed peer as the source, so
    // the pipeline's STUN/ICE and source-filtered paths see the peer — never the TURN server the datagram
    // physically arrived from. The peer is the transport's remote endpoint (guaranteed set for a relay
    // transport by the constructor); the `?? source` is unreachable in relay mode and only a defensive
    // fallback. Anything not relayed through our channel did not traverse the relay and is dropped. Direct
    // mode passes through unchanged.
    private void DeliverInbound(ReadOnlySpan<byte> datagram, IPEndPoint source)
    {
        if (Volatile.Read(ref _relayServer) is { } relayServer)
        {
            // Data path: once a channel is bound, ChannelData for it is media. Present the bound peer as the
            // source (guaranteed set for a relay transport by the constructor / EnterRelayMode) so the pipeline's
            // STUN/ICE and source-filtered paths see the peer, never the TURN server it physically arrived from.
            if (Volatile.Read(ref _relay) is { } relay && relay.TryUnwrap(datagram, source, out var payload))
            {
                _inbound.ProcessInboundDatagram(payload, Volatile.Read(ref _remoteEndPoint) ?? source);
                return;
            }

            // Not our ChannelData (or no channel bound yet). From the relay server the remaining traffic is
            // the TURN control plane — STUN Allocate/Permission/ChannelBind/Refresh responses (and
            // Data-Indications) that ride the same 5-tuple. Hand them to the control callback to match by
            // transaction id; the transport stays agnostic. Anything else did not come through the relay.
            if (Volatile.Read(ref _onRelayControl) is { } onControl
                && RelayEndPoint.SameEndPoint(source, relayServer)
                && MediaPacketClassifier.Classify(datagram) == MediaPacketKind.Stun)
            {
                onControl(datagram.ToArray());
            }

            return;
        }

        // Direct mode. When a per-pair relay indication path is active (a relay ICE local candidate is being
        // checked), datagrams from its relay server are the relayed control/data plane and must not be treated
        // as direct peer traffic; everything else is a direct (host/srflx) peer datagram.
        if (Volatile.Read(ref _indicationRelay) is { } indication)
        {
            if (indication.TryUnwrap(datagram, source, out var peer, out var inner) && peer is not null)
            {
                // A relayed Data indication: the inner payload came from `peer` through the server. Present the
                // peer as the source so the ICE/DTLS layers see the peer, never the TURN server it arrived from.
                _inbound.ProcessInboundDatagram(inner, peer);
                return;
            }

            if (indication.IsFromRelay(source))
            {
                // From the relay server but not a Data indication: a TURN control response
                // (CreatePermission/Refresh). Match it by transaction id via the control callback, or drop it —
                // it is never direct peer media, so it must not reach the media pipeline.
                if (Volatile.Read(ref _onIndicationControl) is { } onControl
                    && MediaPacketClassifier.Classify(datagram) == MediaPacketKind.Stun)
                {
                    onControl(datagram.ToArray());
                }
                else
                {
                    _logger.LogTrace(
                        "Dropping non-control datagram from relay server on {LocalEndPoint}.", _localEndPoint);
                }

                return;
            }
        }

        _inbound.ProcessInboundDatagram(datagram, source);
    }

    /// <summary>
    /// Stops the receive loop by cancellation, drains it, then disposes the socket — so the socket is
    /// never disposed underneath a pending receive. The loop swallows the cancellation internally (it
    /// breaks on <see cref="OperationCanceledException"/>) and returns normally, so awaiting it here
    /// does not throw.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        if (_receiveLoop is not null)
            await _receiveLoop.ConfigureAwait(false);

        _loopCts?.Dispose();
        _udp.Dispose();
    }
}
