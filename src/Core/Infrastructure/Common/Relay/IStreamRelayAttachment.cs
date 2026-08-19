using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// A relay ICE local candidate that owns its <em>own</em> transport — a stream relay over a persistent TCP/TLS
/// connection to the TURN server (ADR-073) — adopted by a media session and fed into that session's ICE agent.
/// It is the counterpart of the UDP relay's <see cref="RelayIceBinding"/>: the UDP relay rides the session's
/// shared media socket, so its wiring is expressed as a factory over that socket's send; a stream relay carries
/// its own send and receive, so the session drives it through this seam rather than through the transport.
/// <para>
/// Kept protocol-agnostic in <c>Infrastructure/Common</c> so the media session (<c>Infrastructure/Rtp</c>) can
/// adopt a stream relay without depending on the TURN or WebRTC modules that build it. The concrete
/// implementation lives in the WebRTC composition layer.
/// </para>
/// </summary>
internal interface IStreamRelayAttachment : IAsyncDisposable
{
    /// <summary>The relayed transport address the TURN server allocated — the relay candidate's advertised address.</summary>
    IPEndPoint RelayedEndPoint { get; }

    /// <summary>The relay local candidate's TURN-framed send path (RFC 8656 §10) — <c>(datagram, remoteTarget, ct)</c>.</summary>
    Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> RelaySend { get; }

    /// <summary>
    /// Installs a TURN permission (RFC 8656 §9) for a peer IP over the allocation, deduplicated per IP, or
    /// <see langword="null"/> when proactive permissioning is off. A controlled (answerer) agent uses it to
    /// permission the offerer's remote-candidate IPs so their inbound relay checks are not dropped by the server.
    /// </summary>
    Func<IPAddress, CancellationToken, Task>? EnsurePermission { get; }

    /// <summary>
    /// Wires the inbound route for relayed connectivity checks and starts the relay transport's receive loop.
    /// Called by the adopting session with a sink that hands each unwrapped relayed Data indication
    /// (<c>peer, inner datagram</c>) to the ICE agent. Idempotent. Must run before the relay candidate is checked
    /// so the inbound route exists before the first check is sent.
    /// </summary>
    /// <param name="onInboundIndication">Sink for the peer and inner payload of each relayed Data indication.</param>
    void Activate(Action<IPEndPoint, byte[]> onInboundIndication);

    /// <summary>Starts the relay allocation/permission keepalive (RFC 8656 §3.9/§9), if any. Idempotent.</summary>
    void StartKeepAlive();

    /// <summary>
    /// Transitions this stream relay onto the media path once its pair is nominated: ChannelBinds
    /// <paramref name="peer"/> (RFC 8656 §11) over the stream, installs the bound channel, re-points relayed
    /// inbound media at <paramref name="onInboundMedia"/>, and starts the channel keepalive (§12). Returns the
    /// stream's ChannelData media send — <c>(datagram, ct)</c> — for the session to route media through (its own
    /// transport then forwards media there instead of the UDP socket), or <see langword="null"/> when the relay
    /// cannot bind a channel, leaving media on the direct path (which then fails consent, ADR-073 §3).
    /// </summary>
    /// <param name="peer">The nominated remote to bind a relay channel to.</param>
    /// <param name="onInboundMedia">The session's relayed-media sink (inbound ChannelData inner, attributed to the peer).</param>
    /// <param name="ct">Cancellation token — the session cancels an in-flight transition before teardown.</param>
    Task<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>?> BindChannelAsync(
        IPEndPoint peer, Action<byte[]> onInboundMedia, CancellationToken ct);
}
