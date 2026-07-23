using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// Immutable configuration a <see cref="TurnServerHost"/> is built from (Level 2). Mirrors the client facades'
/// configuration objects (<c>VoipConfiguration</c>, <c>WebRtcConfiguration</c>): the mutable
/// <see cref="TurnServerHostOptions"/> maps onto this for the DI happy path.
/// </summary>
public sealed class TurnServerHostConfiguration
{
    /// <summary>The endpoint the server binds to. Use port <c>0</c> for an ephemeral port (read back via LocalEndPoint).</summary>
    public required IPEndPoint BindEndPoint { get; init; }

    /// <summary>The transport the server listens on (UDP, TCP, or TLS). TLS requires <see cref="TlsCertificate"/>.</summary>
    public IceTransport Transport { get; init; } = IceTransport.Udp;

    /// <summary>
    /// Whether clients must authenticate with long-term credentials (RFC 8656 §9.2). When <see langword="true"/>
    /// a <see cref="Realm"/> and at least one <see cref="Credentials">credential</see> are required.
    /// </summary>
    public bool RequireAuthentication { get; init; } = true;

    /// <summary>The realm returned in 401 challenges and used for long-term key derivation. Required when authenticating.</summary>
    public string? Realm { get; init; }

    /// <summary>The long-term credentials the server accepts. Required (non-empty) when authenticating.</summary>
    public IReadOnlyList<TurnServerCredential> Credentials { get; init; } = [];

    /// <summary>The server certificate for a TLS transport; ignored for UDP/TCP. Required when <see cref="Transport"/> is TLS.</summary>
    public X509Certificate2? TlsCertificate { get; init; }

    /// <summary>
    /// The public IP address advertised to clients in XOR-RELAYED-ADDRESS instead of the bound/routed
    /// relay address (RFC 8656 §7.2). Set this in NAT'd, multi-homed, or cloud deployments where the
    /// relay socket's local address is not reachable by remote peers. When null the server derives the
    /// advertised address automatically and warns if it can only fall back to loopback.
    /// </summary>
    public IPAddress? PublicRelayAddress { get; init; }

    /// <summary>The allocation lifetime granted when a client does not request one (RFC 8656 §3.9). Default 600 s.</summary>
    public uint DefaultAllocationLifetimeSeconds { get; init; } = 600;

    /// <summary>The maximum allocation lifetime a client may request. Default 3600 s.</summary>
    public uint MaxAllocationLifetimeSeconds { get; init; } = 3600;

    /// <summary>The maximum number of concurrent allocations the server admits. Default 16384.</summary>
    public int MaxTotalAllocations { get; init; } = 16384;

    /// <summary>The logger factory for server diagnostics; a no-op factory is used when null.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
