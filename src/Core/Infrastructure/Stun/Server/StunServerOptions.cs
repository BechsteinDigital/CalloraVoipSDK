namespace CalloraVoipSdk.Core.Infrastructure.Stun.Server;

/// <summary>
/// Runtime limits and backpressure configuration for <see cref="StunServer"/>.
/// </summary>
internal sealed class StunServerOptions
{
    /// <summary>
    /// Default options instance used when no explicit configuration is supplied.
    /// </summary>
    public static StunServerOptions Default { get; } = new();

    /// <summary>
    /// TCP listener backlog passed to <see cref="System.Net.Sockets.TcpListener.Start(int)"/>.
    /// Must be positive.
    /// </summary>
    public int TcpListenBacklog { get; init; } = 256;

    /// <summary>
    /// Maximum number of simultaneously active TCP/TLS client connections.
    /// Set to 0 for unlimited.
    /// </summary>
    public int MaxConcurrentStreamConnections { get; init; } = 1024;

    /// <summary>
    /// Connection handling behavior when <see cref="MaxConcurrentStreamConnections"/> is reached.
    /// </summary>
    public StunConnectionCapPolicy ConnectionCapPolicy { get; init; } = StunConnectionCapPolicy.Backpressure;

    /// <summary>
    /// Maximum number of concurrently processed UDP datagrams.
    /// Set to 0 for unlimited processing (not recommended for production).
    /// </summary>
    public int MaxConcurrentUdpPacketHandlers { get; init; } = 256;

    /// <summary>
    /// Maximum time a single TCP/TLS client may take to complete the TLS handshake before its
    /// connection is dropped. Without this bound a peer that dribbles the TLS ClientHello (or opens
    /// the socket and stalls) holds a connection slot indefinitely — the slot cap alone does not stop
    /// a slowloris, it just caps how many slots the attacker must hold to exhaust the server (K4).
    /// Applies to TLS transport only. Set to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// or zero to disable.
    /// </summary>
    public TimeSpan StreamHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum time a single TCP/TLS message read may take before the connection is dropped. Bounds a
    /// slowloris that dribbles a partial STUN message byte-by-byte, and an idle connection that holds a
    /// slot without delivering a request (RFC 5389 §7.2.2 stream framing; K4). The deadline is applied
    /// per message, so a client that promptly sends complete requests is unaffected. Set to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> or zero to disable.
    /// </summary>
    public TimeSpan StreamReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
