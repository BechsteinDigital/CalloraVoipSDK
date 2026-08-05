using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Routing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Abstraction over SIP datagram transport runtime.
/// Supports dependency injection and deterministic test doubles.
/// </summary>
internal interface ISipTransportRuntime : IDisposable
{
    /// <summary>
    /// Local endpoint currently bound for SIP datagram I/O.
    /// </summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Subscribes to parsed SIP request messages. The <see cref="SipInboundRequestContext"/> carries the
    /// real receive transport and, for connection-oriented transports, the accepted inbound connection id
    /// so a response can be routed back over that exact connection (#158 P1-2).
    /// </summary>
    IDisposable SubscribeRequests(Action<SipInboundRequestContext, SipRequest> handler);

    /// <summary>
    /// Subscribes to parsed SIP response messages.
    /// </summary>
    IDisposable SubscribeResponses(Action<IPEndPoint, SipResponse> handler);

    /// <summary>
    /// Sends a SIP request datagram to a remote endpoint.
    /// </summary>
    Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a SIP request over an explicit transport protocol.
    /// </summary>
    Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a SIP response datagram to a remote endpoint.
    /// </summary>
    Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a SIP response over an explicit transport protocol. When <paramref name="inboundConnectionId"/>
    /// identifies a live accepted inbound connection and the transport is connection-oriented, the response
    /// is sent back over that exact connection rather than a fresh outbound connection (#158 P1-2).
    /// </summary>
    Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        int? inboundConnectionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the network endpoint for a SIP host/port target.
    /// </summary>
    Task<IPEndPoint> ResolveRemoteEndPointAsync(
        string host,
        int port,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a remote endpoint for a specific transport protocol.
    /// </summary>
    Task<IPEndPoint> ResolveRemoteEndPointAsync(
        string host,
        int port,
        SipTransportProtocol transport,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves ordered remote route candidates for one host/port/transport target.
    /// </summary>
    async Task<IReadOnlyList<SipRouteCandidate>> ResolveRemoteRouteCandidatesAsync(
        string host,
        int port,
        SipTransportProtocol transport,
        CancellationToken ct = default)
    {
        var endpoint = await ResolveRemoteEndPointAsync(host, port, transport, ct).ConfigureAwait(false);
        return
        [
            new SipRouteCandidate
            {
                EndPoint = endpoint,
                Transport = transport,
                Source = "transport-default"
            }
        ];
    }

    /// <summary>
    /// Returns local endpoint bound for a specific transport protocol.
    /// </summary>
    IPEndPoint GetLocalEndPoint(SipTransportProtocol transport);
}
