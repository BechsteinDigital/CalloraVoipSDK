using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// A permission-based relay indication channel: it frames an outbound datagram for a <em>specific</em> peer
/// (the peer travels in each framed datagram) and recovers the peer and inner payload from datagrams relayed
/// back through the relay server. Unlike <see cref="IRelayDatagramChannel"/> — which is bound to one peer and
/// carries every send to it uniformly — this addresses any permitted peer per datagram, which is what the ICE
/// checking phase needs: connectivity checks go to several remote candidates over one allocation before any
/// pair is nominated.
///
/// Kept in <c>Infrastructure/Common</c> so a media transport (<c>Infrastructure/Rtp</c>) can unwrap relayed
/// indications through this protocol-agnostic seam rather than depending directly on the TURN module. The TURN
/// implementation (RFC 8656 §10 Send/Data indications) is <c>TurnRelayIndicationChannel</c>.
/// </summary>
internal interface IRelayIndicationChannel
{
    /// <summary>The relay server endpoint that framed datagrams are sent to and relayed datagrams arrive from.</summary>
    IPEndPoint RelayServer { get; }

    /// <summary>
    /// Whether an inbound datagram's <paramref name="source"/> is this channel's relay server (the same
    /// dual-stack-robust match <see cref="TryUnwrap"/> applies). Lets a transport route relay-server control
    /// traffic (TURN CreatePermission/Refresh responses) that is not a relayed Data indication.
    /// </summary>
    /// <param name="source">The inbound datagram's source endpoint.</param>
    bool IsFromRelay(IPEndPoint source);

    /// <summary>
    /// Frames <paramref name="payload"/> for delivery to <paramref name="peer"/>. The returned datagram is
    /// addressed to <see cref="RelayServer"/>, which forwards the payload to the peer (a permission for the
    /// peer must exist, RFC 8656 §9).
    /// </summary>
    /// <param name="peer">The peer the relay should forward the payload to.</param>
    /// <param name="payload">The media/transport bytes to relay.</param>
    /// <returns>The framed datagram to send to the relay server.</returns>
    byte[] Wrap(IPEndPoint peer, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Recovers the relayed peer and inner payload from an inbound datagram. Succeeds only when the datagram
    /// came from <see cref="RelayServer"/> and is a relayed Data indication; otherwise the caller handles the
    /// datagram directly, as it did not traverse the relay.
    /// </summary>
    /// <param name="datagram">The raw inbound datagram.</param>
    /// <param name="source">The datagram's source endpoint (must be the relay server).</param>
    /// <param name="peer">The peer the payload was relayed from, or <see langword="null"/> when this returns false.</param>
    /// <param name="payload">The recovered inner payload, or an empty array when this returns false.</param>
    /// <returns><see langword="true"/> when the datagram is a Data indication relayed for us.</returns>
    bool TryUnwrap(ReadOnlySpan<byte> datagram, IPEndPoint source, out IPEndPoint? peer, out byte[] payload);
}
