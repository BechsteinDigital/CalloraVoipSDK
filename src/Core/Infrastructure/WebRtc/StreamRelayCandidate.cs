using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// A gathered stream relay ICE candidate (ADR-073 slice 4c-i, #240): a TURN relay allocation obtained over a
/// persistent TCP/TLS connection to the server, together with the <see cref="StreamRelayMediaTransport"/> that
/// carries it and the transport-agnostic <see cref="RelayIceBinding"/> that drives its permissions, send path,
/// channel bind and keepalive. It is the stream analog of the UDP relay path's gathered allocation — but where
/// the UDP relay rides the session's shared media socket, this owns its own connection (ADR-073 decision 2:
/// libwebrtc's per-server-Port model), so the candidate carries its transport with it.
/// <para>
/// Produced by <see cref="WebRtcStreamRelayGatherer"/>. It is inert until <see cref="Activate"/> wires its
/// inbound and starts the transport's receive loop — activation is the consumer's, so the inbound route into the
/// live ICE agent's consent/nomination is installed <em>before</em> any connectivity check is sent (closing the
/// window in which an inbound relayed Data indication could fall through to the control path). The relay send
/// path, permission installer and channel-bind seam are exposed via <see cref="Binding"/> for the ICE-agent
/// wiring (slice 4c-ii); this type owns only the transport-and-keepalive lifecycle and its teardown order.
/// </para>
/// </summary>
internal sealed class StreamRelayCandidate : IAsyncDisposable
{
    private readonly StreamRelayMediaTransport _transport;
    private int _activated;
    private int _disposed;

    internal StreamRelayCandidate(
        StreamRelayMediaTransport transport,
        RelayIceBinding binding,
        IPEndPoint relayedEndPoint,
        IPEndPoint serverEndPoint)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        RelayedEndPoint = relayedEndPoint ?? throw new ArgumentNullException(nameof(relayedEndPoint));
        ServerEndPoint = serverEndPoint ?? throw new ArgumentNullException(nameof(serverEndPoint));
    }

    /// <summary>The relay binding — its send path, permission installer, channel-bind seam and keepalive.</summary>
    public RelayIceBinding Binding { get; }

    /// <summary>The relayed transport address the TURN server allocated (this candidate's advertised address).</summary>
    public IPEndPoint RelayedEndPoint { get; }

    /// <summary>The TURN server's transport address the allocation lives on.</summary>
    public IPEndPoint ServerEndPoint { get; }

    /// <summary>
    /// Wires the transport's inbound relayed Data indications to <paramref name="onInboundIndication"/> (a
    /// connectivity-check request or response from a peer, RFC 8656 §10) and starts the receive loop. Call this
    /// once, before handing the send path to the ICE agent, so the inbound route into consent/nomination exists
    /// before the first check is sent. Idempotent: a second call is a no-op.
    /// </summary>
    /// <param name="onInboundIndication">Invoked with the peer and inner payload of each relayed Data indication.</param>
    public void Activate(Action<IPEndPoint, byte[]> onInboundIndication)
    {
        ArgumentNullException.ThrowIfNull(onInboundIndication);
        if (Interlocked.Exchange(ref _activated, 1) != 0)
            return;
        _transport.SetIndicationRelay(Binding.Indication, onInboundIndication);
        _transport.Start();
    }

    /// <summary>
    /// Starts the allocation/permission keepalive (RFC 8656 §3.9/§9), if the binding supplies one. Called once
    /// the session is up; idempotent (the keepalive loop guards its own start). The keepalive rides the
    /// transport's send, so <see cref="DisposeAsync"/> disposes it — running its teardown — before the transport.
    /// </summary>
    public void StartKeepAlive() => Binding.KeepAlive?.Start();

    /// <summary>
    /// Tears the candidate down. The keepalive is disposed first — its teardown Refresh(0) rides the transport's
    /// send, so it must run while the transport is still alive — and only then is the transport (and with it the
    /// stream) disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (Binding.KeepAlive is { } keepAlive)
            await keepAlive.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
