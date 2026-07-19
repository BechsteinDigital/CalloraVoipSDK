using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// A bound TURN channel over UDP (RFC 8656 §11–12): the data-path counterpart to the allocation,
/// permission and channel-bind control path on <see cref="ITurnClient"/>. It frames outbound media as
/// ChannelData addressed to the relay server, and recovers the media payload from inbound ChannelData
/// relayed back from that same server.
///
/// It performs no I/O and holds no socket — it only translates datagrams — so a media transport can drop
/// it in below its packet demux and relay every datagram (STUN connectivity checks, DTLS flights, RTP/RTCP)
/// uniformly through the one bound channel. Wiring it into the transport, plus the allocation/permission/
/// channel-bind lifecycle and inbound Data-Indication handling, are later slices; this type is the wrap/
/// unwrap primitive they build on.
///
/// <see cref="TryUnwrap"/> accepts a relayed datagram only when it arrives from the relay server's exact
/// 5-tuple and is ChannelData for this channel — the source filter that keeps an off-path attacker from
/// injecting media by forging ChannelData, mirroring the DTLS/SRTP source discipline elsewhere in the
/// bundle transport.
/// </summary>
internal sealed class TurnRelayChannel : IRelayDatagramChannel
{
    private readonly IPEndPoint _relayServer;
    private readonly ushort _channelNumber;

    /// <summary>
    /// Creates a relay channel bound to <paramref name="channelNumber"/> on <paramref name="relayServer"/>.
    /// </summary>
    /// <param name="relayServer">The TURN server's 5-tuple that relayed traffic flows through.</param>
    /// <param name="channelNumber">The bound channel number; must be in the TURN range 0x4000..0x7FFF.</param>
    /// <exception cref="ArgumentNullException"><paramref name="relayServer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelNumber"/> is outside 0x4000..0x7FFF.</exception>
    public TurnRelayChannel(IPEndPoint relayServer, ushort channelNumber)
    {
        ArgumentNullException.ThrowIfNull(relayServer);
        if (channelNumber < 0x4000 || channelNumber > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(channelNumber), "TURN channel number must be in range 0x4000..0x7FFF.");

        _relayServer = relayServer;
        _channelNumber = channelNumber;
    }

    /// <summary>The relay server's endpoint; framed datagrams from <see cref="Wrap"/> are sent here.</summary>
    public IPEndPoint RelayServer => _relayServer;

    /// <summary>The bound channel number (0x4000..0x7FFF).</summary>
    public ushort ChannelNumber => _channelNumber;

    /// <summary>
    /// Frames a media payload as a ChannelData packet for this channel. The returned datagram is addressed
    /// to <see cref="RelayServer"/>.
    /// </summary>
    /// <remarks>
    /// Allocates the framed datagram per call (via <see cref="TurnChannelDataCodec.Encode"/>). A pooled,
    /// span-writing overload for the per-packet send hotpath is a later optimisation.
    /// </remarks>
    /// <param name="payload">The media (RTP/RTCP) or transport (STUN/DTLS) bytes to relay.</param>
    /// <returns>The ChannelData datagram to send to the relay server.</returns>
    public byte[] Wrap(ReadOnlySpan<byte> payload) => TurnChannelDataCodec.Encode(_channelNumber, payload);

    /// <summary>
    /// Recovers the relayed media payload from an inbound datagram. Succeeds only when the datagram came
    /// from <see cref="RelayServer"/> and is ChannelData for this channel; the peer address is implicit in
    /// the channel binding, so it is not returned.
    /// </summary>
    /// <param name="datagram">The raw inbound datagram from the socket.</param>
    /// <param name="source">
    /// The datagram's source endpoint. An IPv4-mapped-IPv6 form of the relay address (as a dual-stack
    /// socket may surface it) is treated as equal to a plain IPv4 relay endpoint.
    /// </param>
    /// <param name="payload">The recovered inner media payload, or an empty array when this returns false.</param>
    /// <returns>
    /// <see langword="true"/> when the datagram is relayed traffic for this channel; <see langword="false"/>
    /// when it is from another source or not this channel's ChannelData — the caller then handles it
    /// directly (it did not traverse the relay).
    /// </returns>
    public bool TryUnwrap(ReadOnlySpan<byte> datagram, IPEndPoint source, out byte[] payload)
    {
        payload = Array.Empty<byte>();

        ArgumentNullException.ThrowIfNull(source);
        if (!SameEndPoint(source, _relayServer))
            return false;

        if (!TurnChannelDataCodec.TryParse(datagram, out var channel, out var data) || channel != _channelNumber)
            return false;

        payload = data;
        return true;
    }

    // An IPv4 relay endpoint and an IPv4-mapped-IPv6 source (::ffff:a.b.c.d — how a dual-stack socket can
    // surface the same host) denote the same peer. Canonicalise both addresses before comparing so the
    // source filter neither drops genuine relayed traffic on a dual-stack socket nor lets a different host
    // through (mapping is a lossless, host-preserving transform).
    private static bool SameEndPoint(IPEndPoint a, IPEndPoint b)
        => a.Port == b.Port && Canonical(a.Address).Equals(Canonical(b.Address));

    private static IPAddress Canonical(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
