namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Listener configuration for <see cref="SipTransportRuntime"/>: which local ports to bind, plus the
/// admission and slowloris limits for inbound connection-oriented transports (TCP/TLS/WS/WSS). The limits
/// bound the resources a remote peer can pin — how many accepted connections may be live at once (globally
/// and per source IP), and how long a peer may take to complete the TLS handshake or WebSocket upgrade
/// before its connection, and the admission slot it holds, is dropped (#158 P1-3, K4). UDP is
/// connectionless and unaffected by the limits, but does honour <see cref="LocalSipPort"/>.
/// </summary>
internal sealed class SipTransportOptions
{
    /// <summary>
    /// Default options instance used when no explicit configuration is supplied.
    /// </summary>
    public static SipTransportOptions Default { get; } = new();

    /// <summary>
    /// Maximum number of simultaneously accepted inbound stream/WebSocket connections across all remotes.
    /// A newly accepted connection beyond this cap is dropped. Set to 0 for unlimited (not recommended).
    /// </summary>
    public int MaxConcurrentInboundConnections { get; init; } = 1024;

    /// <summary>
    /// Maximum number of simultaneously accepted inbound stream/WebSocket connections from a single source
    /// IP address, so one peer cannot consume the whole global budget (amplification/DoS). A connection
    /// beyond this per-remote cap is dropped. Set to 0 for unlimited.
    /// </summary>
    public int MaxInboundConnectionsPerRemote { get; init; } = 32;

    /// <summary>
    /// Maximum number of entries the runtime keeps in each learned endpoint hint map (remote endpoint →
    /// transport, and resolved endpoint → TLS SNI host). These are optimisation caches — a missing entry
    /// falls back to the default transport / the literal IP for TLS — so they are bounded to stop a peer that
    /// spoofs many source addresses from growing them without limit (#158 P1-4). Set to 0 for unlimited.
    /// </summary>
    public int MaxEndpointHintEntries { get; init; } = 4096;

    /// <summary>
    /// Maximum time an accepted connection may take to complete its TLS handshake (TLS/WSS) or WebSocket
    /// upgrade (WS/WSS) before it is dropped and its admission slot released. Without this bound a peer that
    /// dribbles or stalls the handshake holds a slot indefinitely — the slot cap alone does not stop a
    /// slowloris, it only caps how many slots the attacker must hold to exhaust the server (K4). Plain TCP
    /// has no handshake and is unaffected. Set to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> or
    /// zero to disable.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Local port the UDP and TCP SIP listeners bind to. <c>0</c> (default) takes an ephemeral port, which
    /// is what a registering line wants — the registrar learns the port from the REGISTER Contact.
    /// </summary>
    /// <remarks>
    /// A fixed port is required when nobody tells the peer where to reach us: an IP-authenticated trunk
    /// (<c>SipAccount.Register = false</c>, #104) sends no REGISTER, so the provider delivers inbound calls
    /// to a pre-agreed address — normally 5060. An ephemeral port also changes on every restart, so any
    /// static firewall or NAT rule would go stale.
    /// </remarks>
    public int LocalSipPort { get; init; }

    /// <summary>
    /// Local port the TLS SIP listener binds to. <c>0</c> (default) takes an ephemeral port. Separate from
    /// <see cref="LocalSipPort"/> because TLS is a second TCP listener and cannot share that port; the SIP
    /// convention is 5061 next to 5060 (RFC 3261 §19.1.2).
    /// </summary>
    public int LocalSipTlsPort { get; init; }
}
