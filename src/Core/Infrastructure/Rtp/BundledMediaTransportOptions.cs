using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Configuration for a <see cref="BundledMediaTransport"/> — the one shared 5-tuple a BUNDLE group
/// (RFC 8843) runs over. The remote endpoint is the initial send target; once ICE nominates a pair or
/// symmetric latching kicks in (later slices), the transport retargets sends accordingly.
/// </summary>
internal sealed class BundledMediaTransportOptions
{
    /// <summary>The local endpoint the shared UDP socket binds to.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }

    /// <summary>
    /// The initial remote endpoint sends are directed to, or <see langword="null"/> when the peer
    /// address is not known yet (ICE will supply it). Sends are suppressed while it is null.
    /// </summary>
    public IPEndPoint? RemoteEndPoint { get; init; }

    /// <summary>
    /// The TURN relay server's transport address. When set, the transport runs in relay mode: TURN control
    /// requests can be sent (<see cref="BundledMediaTransport.SendControlAsync"/>) and control responses from
    /// this address are surfaced via <see cref="OnRelayControl"/>, even before a channel is bound — this is
    /// the control phase during which the allocation is established. <see langword="null"/> is direct
    /// (host/srflx) transport. If only <see cref="Relay"/> is supplied, the server address is taken from it.
    /// </summary>
    public IPEndPoint? RelayServer { get; init; }

    /// <summary>
    /// The bound relay channel. When set, every outbound datagram is framed as ChannelData to the relay
    /// server and every inbound one is unwrapped from it (RFC 8656 §11–12), so STUN checks, DTLS flights and
    /// RTP/RTCP all traverse the one bound channel. It may be supplied up front (the channel is already
    /// known) or installed after the channel-bind via <see cref="BundledMediaTransport.SetRelayChannel"/>
    /// once the allocation completes; until then, in relay mode, outbound media is suppressed.
    /// </summary>
    public IRelayDatagramChannel? Relay { get; init; }

    /// <summary>
    /// Invoked (relay mode only) with each inbound STUN datagram from the relay server that is not
    /// ChannelData — the TURN control plane (Allocate/CreatePermission/ChannelBind responses, and
    /// Data-Indications). The allocation orchestrator matches responses by transaction id and interprets
    /// Data-Indications; the transport stays agnostic. <see langword="null"/> drops such datagrams (the
    /// data-only relay wiring of Slice 4b).
    /// </summary>
    public Action<ReadOnlyMemory<byte>>? OnRelayControl { get; init; }
}
