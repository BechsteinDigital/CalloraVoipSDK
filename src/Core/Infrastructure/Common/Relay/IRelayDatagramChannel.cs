using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// A relay data-path channel: frames an outbound datagram for a relay server and recovers the inner payload
/// from datagrams relayed back through it. It lets a media transport relay every datagram it carries — STUN
/// connectivity checks, DTLS flights, RTP/RTCP — uniformly through one channel without depending on a
/// specific relay protocol. The TURN implementation (RFC 8656 ChannelData) is <c>TurnRelayChannel</c>.
///
/// Kept in <c>Infrastructure/Common</c> so the media transport (<c>Infrastructure/Rtp</c>) depends on this
/// protocol-agnostic seam rather than directly on the TURN module.
/// </summary>
internal interface IRelayDatagramChannel
{
    /// <summary>The relay server endpoint that framed datagrams are sent to.</summary>
    IPEndPoint RelayServer { get; }

    /// <summary>
    /// Whether an inbound datagram's <paramref name="source"/> is this channel's relay server (the same
    /// dual-stack-robust match <see cref="TryUnwrap"/> applies). Lets a transport route relay-server control
    /// traffic (TURN responses / Data-Indications) that is not ChannelData.
    /// </summary>
    bool IsFromRelay(IPEndPoint source);

    /// <summary>Frames a payload for the relay server. The returned datagram is addressed to <see cref="RelayServer"/>.</summary>
    /// <param name="payload">The media/transport bytes to relay.</param>
    /// <returns>The framed datagram to send to the relay server.</returns>
    byte[] Wrap(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Recovers the inner payload from an inbound datagram. Succeeds only when the datagram is relayed
    /// traffic for this channel (from the relay server, correctly framed); otherwise the caller handles the
    /// datagram directly, as it did not traverse the relay.
    /// </summary>
    /// <param name="datagram">The raw inbound datagram.</param>
    /// <param name="source">The datagram's source endpoint.</param>
    /// <param name="payload">The recovered inner payload, or an empty array when this returns false.</param>
    /// <returns><see langword="true"/> when the datagram is relayed traffic for this channel.</returns>
    bool TryUnwrap(ReadOnlySpan<byte> datagram, IPEndPoint source, out byte[] payload);
}
