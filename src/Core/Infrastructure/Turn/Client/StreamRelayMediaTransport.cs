using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// A TURN relay media transport over a persistent TCP/TLS stream (RFC 8656 §2.1 client-server transport,
/// ChannelData framed per §12.5) — the stream counterpart of the UDP <c>BundledMediaTransport</c> relay path
/// (ADR-073). Unlike the UDP transport there is no shared media socket to transition in place: the stream to
/// the TURN server <em>is</em> the transport, so this is a distinct transport selected by ICE nomination, its
/// receive path the stream rather than a socket (ADR-073 decision 2 — libwebrtc's per-server-Port model).
/// <para>
/// It satisfies <see cref="IRelayControlTransport"/> so the same transport-agnostic
/// <see cref="TurnRelayCoordinator"/> that drives the UDP path drives this one: <see cref="SendControlAsync"/>
/// writes a TURN request as a stream frame (STUN is self-framing by length, RFC 8489 §5), control responses
/// and Data-Indications surface on the receive loop via the injected control callback, and once ChannelBind
/// completes <see cref="SetRelayChannel"/> installs the bound channel and the data phase opens. All stream
/// writes are serialized (a stream is not safe for concurrent writers); the installed channel is read via
/// <see cref="Volatile"/> because the receive loop and the send paths race the nomination thread that sets it.
/// </para>
/// <para>
/// v1 lifecycle is fail-fast (ADR-073 decision 3): a broken stream ends the receive loop and surfaces the
/// error; there is no transparent reconnect (a new connection is a new allocation), matching libwebrtc
/// (<c>TurnPort::OnSocketClose → Close()</c>) and pjnath (<c>sess_fail → destroy</c>). Recovery is ICE
/// restart (ADR-072).
/// </para>
/// </summary>
internal sealed class StreamRelayMediaTransport : IRelayControlTransport, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IPEndPoint _relayServer;
    private readonly Action<byte[]> _onRelayControl;
    private readonly Action<byte[]> _onInboundMedia;
    private readonly ILogger<StreamRelayMediaTransport> _logger;

    // A stream permits only one writer at a time; every write (control and data) takes this so a control
    // request cannot interleave its bytes with a ChannelData frame on the wire.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // The bound relay channel, installed after ChannelBind. Read on the send path and written by the
    // nomination/coordinator thread — Volatile so a sender observing non-null sees a fully constructed channel.
    private IRelayDatagramChannel? _channel;

    // The per-peer indication channel used during the ICE checking phase (RFC 8656 §10): relayed Data
    // indications carry a connectivity-check response from a specific peer. Installed before checks and read on
    // the receive loop — Volatile so the loop observing non-null sees a fully constructed channel. Distinct from
    // the post-nomination ChannelData path (_channel), which the framer separates by frame type.
    private IRelayIndicationChannel? _indicationRelay;
    private Action<IPEndPoint, byte[]>? _onInboundIndication;

    private Task? _receiveLoop;
    private int _disposed;

    /// <summary>Creates the stream relay transport over an already-connected TCP/TLS stream to the TURN server.</summary>
    /// <param name="stream">The connected stream to the TURN server. The transport owns its read/write, not its lifetime beyond dispose.</param>
    /// <param name="relayServer">The TURN server endpoint (for the installed channel's server match).</param>
    /// <param name="onRelayControl">Invoked with each inbound STUN control datagram (TURN responses / Data-Indications) read off the stream.</param>
    /// <param name="onInboundMedia">Invoked with the inner payload of each inbound ChannelData frame (relayed media).</param>
    /// <param name="logger">Diagnostics sink.</param>
    public StreamRelayMediaTransport(
        Stream stream,
        IPEndPoint relayServer,
        Action<byte[]> onRelayControl,
        Action<byte[]> onInboundMedia,
        ILogger<StreamRelayMediaTransport> logger)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _relayServer = relayServer ?? throw new ArgumentNullException(nameof(relayServer));
        _onRelayControl = onRelayControl ?? throw new ArgumentNullException(nameof(onRelayControl));
        _onInboundMedia = onInboundMedia ?? throw new ArgumentNullException(nameof(onInboundMedia));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Starts the receive loop that reads framed messages off the stream. Idempotent per instance.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _receiveLoop ??= Task.Run(() => ReceiveLoopAsync(_lifetime.Token));
    }

    /// <inheritdoc />
    public async ValueTask SendControlAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        // STUN messages are self-framing over a stream (RFC 8489 §5: 20-byte header carries the body length,
        // already 4-byte aligned), so the request is written verbatim — the framer reads it back by length.
        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void SetRelayChannel(IRelayDatagramChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Volatile.Write(ref _channel, channel);
    }

    /// <summary>
    /// Installs the per-peer indication channel for the ICE checking phase (RFC 8656 §10): once set, an inbound
    /// relayed Data indication is unwrapped to its inner payload and originating peer and delivered to
    /// <paramref name="onInboundIndication"/> (a connectivity-check response), while other STUN traffic from the
    /// relay server stays on the control path. The receive half of the relay ICE candidate (ADR-054), the
    /// counterpart of the send path's <see cref="TurnRelayCandidateSendPath"/>. Independent of the
    /// post-nomination <see cref="SetRelayChannel"/> data path — the two are separated by frame type.
    /// </summary>
    /// <param name="indication">The indication channel that unwraps Data indications for the allocation's relay server.</param>
    /// <param name="onInboundIndication">Invoked with the peer and inner payload of each relayed Data indication.</param>
    public void SetIndicationRelay(IRelayIndicationChannel indication, Action<IPEndPoint, byte[]> onInboundIndication)
    {
        ArgumentNullException.ThrowIfNull(indication);
        ArgumentNullException.ThrowIfNull(onInboundIndication);
        Volatile.Write(ref _onInboundIndication, onInboundIndication);
        // Publish the callback before the channel discriminator, so the receive loop observing a non-null
        // channel also sees the sink (mirrors the transport's other publish-before-discriminator ordering).
        Volatile.Write(ref _indicationRelay, indication);
    }

    /// <summary>
    /// Sends one media/transport datagram (STUN check, DTLS flight, RTP/RTCP) through the bound channel as
    /// ChannelData over the stream, padded to a 4-byte boundary (RFC 8656 §12.5). Suppressed (no-op) until a
    /// channel is installed, mirroring the UDP transport's suppress-until-bound behaviour.
    /// </summary>
    public async ValueTask SendMediaAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var channel = Volatile.Read(ref _channel);
        if (channel is null)
            return;

        var framed = channel.Wrap(payload.Span);

        // §12.5: over a stream, ChannelData is padded to a 4-byte boundary with 0-3 bytes not counted in the
        // length field. The framer's read side consumes exactly this padding, so the write side must add it.
        int padding = (4 - (framed.Length & 3)) & 3;
        if (padding == 0)
        {
            await WriteAsync(framed, cancellationToken).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[framed.Length + padding];
        framed.CopyTo(buffer, 0);          // trailing pad bytes stay zero
        await WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await TurnStreamFramer.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                if (frame is null)
                {
                    // Clean EOF: the server closed the connection. v1 is fail-fast (ADR-073) — end the loop.
                    _logger.LogInformation("TURN stream relay connection closed by the server.");
                    return;
                }

                if (frame.IsChannelData)
                {
                    _onInboundMedia(frame.Payload);
                    continue;
                }

                // A STUN frame is either a relayed Data indication (a check response from a peer, during the
                // checking phase) or a TURN control response (Allocate/Permission/ChannelBind). The indication
                // channel's TryUnwrap succeeds only for the former; everything else is control. The frame's
                // source is the relay server — the one connection this transport carries.
                var indication = Volatile.Read(ref _indicationRelay);
                if (indication is not null
                    && indication.TryUnwrap(frame.Payload, indication.RelayServer, out var peer, out var inner)
                    && peer is not null)
                {
                    Volatile.Read(ref _onInboundIndication)?.Invoke(peer, inner);
                }
                else
                {
                    _onRelayControl(frame.Payload);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal teardown.
        }
        catch (Exception ex)
        {
            // A stream error is a dropped relay: surface it and let the loop end. ICE consent then fails the
            // relay pair (fail-and-renominate, ADR-073 decision 3) — no transparent reconnect.
            _logger.LogWarning(ex, "TURN stream relay receive loop failed; the relay path is down.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The loop already handles its own faults; awaiting it during teardown can still surface the
                // cancellation (or a late fault) — logged rather than swallowed so a teardown anomaly is visible.
                _logger.LogDebug(ex, "TURN stream relay receive loop ended with an exception during dispose.");
            }
        }

        _lifetime.Dispose();
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
