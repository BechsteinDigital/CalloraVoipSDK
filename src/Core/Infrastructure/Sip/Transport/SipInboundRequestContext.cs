using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Ingress metadata for one inbound SIP request. Carries the real packet source, the transport the request
/// was actually received on — taken from the accepted connection, never reconstructed from the
/// peer-controlled Via header — and, for a connection-oriented transport (TCP/TLS/WS/WSS), the identifier of
/// the accepted inbound connection. The connection identifier lets a response be routed back over the exact
/// connection the request arrived on instead of opening a fresh outbound connection to the peer's ephemeral
/// source port, where no server is listening (#158 P1-2).
/// </summary>
internal readonly record struct SipInboundRequestContext
{
    /// <summary>
    /// Creates one inbound request context.
    /// </summary>
    /// <param name="remoteEndPoint">Actual network source of the received datagram/frame.</param>
    /// <param name="transport">Real transport of the accepted connection (or UDP for connectionless receipt).</param>
    /// <param name="connectionId">
    /// Identifier of the accepted inbound stream/WebSocket connection, or <c>null</c> for connectionless
    /// (UDP) receipt and for frames received on an outbound (pooled) connection.
    /// </param>
    public SipInboundRequestContext(IPEndPoint remoteEndPoint, SipTransportProtocol transport, int? connectionId)
    {
        RemoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
        Transport = transport;
        ConnectionId = connectionId;
    }

    /// <summary>
    /// Actual network source of the received request.
    /// </summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>
    /// Real transport the request was received on (never Via-derived).
    /// </summary>
    public SipTransportProtocol Transport { get; }

    /// <summary>
    /// Accepted inbound connection identifier for connection-oriented transports, otherwise <c>null</c>.
    /// </summary>
    public int? ConnectionId { get; }
}
