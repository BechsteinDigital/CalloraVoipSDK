using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk;

/// <summary>
/// Admission and slowloris limits for inbound connection-oriented SIP transports (TCP/TLS/WS/WSS). UDP is
/// connectionless and unaffected. These bound the resources a remote peer can pin on the SIP listener: how many
/// accepted connections may be live at once (globally and per source IP), how many learned endpoint-hint cache
/// entries the runtime keeps, and how long a peer may take to finish the TLS handshake or WebSocket upgrade
/// before its connection — and the admission slot it holds — is dropped (#158 P1-3/P1-4, K4). Supplied via
/// <see cref="VoipConfiguration.SipTransportHardening"/>; the defaults match the SDK's built-in limits.
/// </summary>
public sealed class SipTransportHardeningConfiguration
{
    /// <summary>
    /// Maximum simultaneously accepted inbound stream/WebSocket connections across all remotes. A newly
    /// accepted connection beyond this cap is dropped. Default 1024; set to 0 for unlimited (not recommended).
    /// </summary>
    public int MaxConcurrentInboundConnections { get; init; } = 1024;

    /// <summary>
    /// Maximum simultaneously accepted inbound stream/WebSocket connections from a single source IP address, so
    /// one peer cannot consume the whole global budget. A connection beyond this per-remote cap is dropped.
    /// Default 32; set to 0 for unlimited.
    /// </summary>
    public int MaxInboundConnectionsPerRemote { get; init; } = 32;

    /// <summary>
    /// Maximum entries the runtime keeps in each learned endpoint-hint map (remote endpoint → transport, and
    /// resolved endpoint → TLS SNI host). These are optimisation caches bounded so a peer spoofing many source
    /// addresses cannot grow them without limit. Default 4096; set to 0 for unlimited.
    /// </summary>
    public int MaxEndpointHintEntries { get; init; } = 4096;

    /// <summary>
    /// Maximum time an accepted connection may take to complete its TLS handshake (TLS/WSS) or WebSocket
    /// upgrade (WS/WSS) before it is dropped and its admission slot released. Plain TCP has no handshake and is
    /// unaffected. Default 10 seconds; set to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> or zero to
    /// disable.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maps this public configuration onto the internal transport options consumed by the SIP runtime.
    /// </summary>
    internal SipTransportOptions ToTransportOptions() => new()
    {
        MaxConcurrentInboundConnections = MaxConcurrentInboundConnections,
        MaxInboundConnectionsPerRemote = MaxInboundConnectionsPerRemote,
        MaxEndpointHintEntries = MaxEndpointHintEntries,
        HandshakeTimeout = HandshakeTimeout,
    };
}
