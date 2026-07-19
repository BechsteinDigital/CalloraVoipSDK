using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// The transport-facing wiring of a relay ICE local candidate, produced by a <see cref="RelayIceBindingFactory"/>
/// once the shared media socket exists. It carries the three seams a media transport and its ICE agent need to
/// relay connectivity checks through a TURN allocation (RFC 8656 §9/§10) without the transport depending on the
/// TURN module: the indication channel (to unwrap inbound relayed Data indications and route relay-server
/// control traffic), the control-response sink (to feed the permission transactor its responses), and the relay
/// send path (the ICE agent's relay-candidate send delegate).
/// </summary>
/// <param name="Indication">The indication channel used to unwrap inbound relayed Data indications (RFC 8656 §10).</param>
/// <param name="OnControl">Sink for the relay server's non-Data STUN control responses (CreatePermission/Refresh).</param>
/// <param name="RelaySend">The relay ICE local candidate's send path — <c>(datagram, remoteTarget, ct)</c>.</param>
/// <param name="KeepAlive">
/// The allocation keepalive (RFC 8656 §3.9 Refresh loop) the media session starts once its transport is up and
/// disposes — running its teardown Refresh(0) — before that transport is torn down. <see langword="null"/> when
/// the producer supplies no keepalive.
/// </param>
internal sealed record RelayIceBinding(
    IRelayIndicationChannel Indication,
    Action<ReadOnlyMemory<byte>> OnControl,
    Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> RelaySend,
    IRelayKeepAlive? KeepAlive = null);
