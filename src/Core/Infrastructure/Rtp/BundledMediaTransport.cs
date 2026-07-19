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
/// When a <see cref="IRelayDatagramChannel"/> is supplied (TURN relay mode, RFC 8656), the transport frames
/// every outbound datagram as ChannelData to the relay server and unwraps every inbound one from it, below
/// the packet demux — so STUN checks, DTLS flights and RTP/RTCP all traverse the one bound channel
/// uniformly and the DTLS/ICE layers above compose unchanged. Obtaining the channel (allocation, permission,
/// channel-bind, refresh) is a later slice.
/// </summary>
internal sealed class BundledMediaTransport : IBundledDatagramSender, IAsyncDisposable
{
    private const int ReceiveBufferSize = 8192;

    private readonly BundledInboundPipeline _inbound;
    private readonly ILogger<BundledMediaTransport> _logger;
    private readonly IPEndPoint _localEndPoint;
    private readonly IRelayDatagramChannel? _relay;
    private readonly UdpClient _udp;

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

        // A relay channel is bound to one peer (RFC 8656 channel-bind is per-peer), so the peer is known
        // whenever relay mode is active. Require it: relayed inbound datagrams physically arrive from the
        // TURN server but must be attributed to that peer (not the server) for the DTLS/ICE source filters
        // above, and there is no correct peer to fabricate otherwise.
        if (_relay is not null && _remoteEndPoint is null)
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
        if (_relay is { } relay)
        {
            // Relay mode: frame as ChannelData and send to the TURN server, which forwards it to the bound
            // peer (RFC 8656 §11–12). The peer is reached through the relay, not the (suppressed) direct remote.
            var framed = relay.Wrap(datagram.Span);
            await _udp.SendAsync(framed, relay.RelayServer, cancellationToken).ConfigureAwait(false);
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

        if (_relay is { } relay)
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

        await _udp.SendAsync(datagram, target, cancellationToken).ConfigureAwait(false);
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
        if (_relay is { } relay)
        {
            if (relay.TryUnwrap(datagram, source, out var payload))
                _inbound.ProcessInboundDatagram(payload, Volatile.Read(ref _remoteEndPoint) ?? source);
            return;
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
