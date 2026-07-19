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
    /// When set, the transport runs in TURN relay mode: every outbound datagram is framed as ChannelData to
    /// the relay server and every inbound datagram is unwrapped from it (RFC 8656 §11–12), so STUN checks,
    /// DTLS flights and RTP/RTCP all traverse the one bound channel. <see langword="null"/> is direct
    /// (host/srflx) transport. Obtaining the channel (allocation, permission, channel-bind, refresh) is a
    /// later slice; this option is the seam the data path plugs into.
    /// </summary>
    public IRelayDatagramChannel? Relay { get; init; }
}
