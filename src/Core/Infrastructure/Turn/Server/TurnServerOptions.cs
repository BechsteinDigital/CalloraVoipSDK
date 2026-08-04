using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Runtime limits and behavior for the TURN server.
/// </summary>
internal sealed class TurnServerOptions
{
    /// <summary>Default options instance.</summary>
    public static TurnServerOptions Default { get; } = new();

    /// <summary>
    /// The public IP address advertised in XOR-RELAYED-ADDRESS (RFC 8656 §7.2) instead of the bound or
    /// routed local relay address. Set this in NAT'd / multi-homed / cloud deployments where the address
    /// the relay socket binds to is not the address remote peers must reach. When <see langword="null"/>
    /// the server derives the advertised address from the routed local interface and, as a last resort,
    /// falls back to loopback with a warning. Only applied when its address family matches the allocation's
    /// relay family (an IPv4 value is ignored for an IPv6 allocation and vice versa).
    /// </summary>
    public IPAddress? PublicRelayAddress { get; init; }

    /// <summary>
    /// TCP listen backlog for stream transports.
    /// </summary>
    public int TcpListenBacklog { get; init; } = 128;

    /// <summary>
    /// Maximum concurrent TCP/TLS client connections.
    /// 0 means unlimited.
    /// </summary>
    public int MaxConcurrentStreamConnections { get; init; } = 1024;

    /// <summary>
    /// Policy used when stream connection cap is reached.
    /// </summary>
    public TurnConnectionCapPolicy ConnectionCapPolicy { get; init; } = TurnConnectionCapPolicy.Backpressure;

    /// <summary>
    /// Maximum number of concurrently processed UDP datagrams.
    /// Set to 0 for unlimited processing (not recommended for production).
    /// </summary>
    public int MaxConcurrentUdpPacketHandlers { get; init; } = 256;

    /// <summary>
    /// Default allocation lifetime returned by Allocate success responses.
    /// </summary>
    public uint DefaultAllocationLifetimeSeconds { get; init; } = 600;

    /// <summary>
    /// Maximum allowed allocation lifetime.
    /// </summary>
    public uint MaxAllocationLifetimeSeconds { get; init; } = 3600;

    /// <summary>
    /// Permission lifetime in seconds.
    /// </summary>
    public uint PermissionLifetimeSeconds { get; init; } = 300;

    /// <summary>
    /// Channel binding lifetime in seconds.
    /// </summary>
    public uint ChannelBindingLifetimeSeconds { get; init; } = 600;

    /// <summary>
    /// Maximum permissions retained per allocation. A client that installs more distinct peer
    /// permissions is refused with 486 Allocation Quota Reached. 0 means unlimited (not recommended).
    /// </summary>
    public int MaxPermissionsPerAllocation { get; init; } = 128;

    /// <summary>
    /// Maximum channel bindings retained per allocation. Binding beyond this is refused with 486
    /// Allocation Quota Reached. 0 means unlimited (not recommended).
    /// </summary>
    public int MaxChannelBindingsPerAllocation { get; init; } = 128;

    /// <summary>
    /// Maximum total concurrent allocations across all clients. Guards against an unbounded
    /// allocation table (e.g. UDP source spoofing). Exceeding it yields 486 Allocation Quota Reached.
    /// 0 means unlimited (not recommended for production).
    /// </summary>
    public int MaxTotalAllocations { get; init; } = 16384;

    /// <summary>
    /// Interval at which a background sweep removes expired allocations and prunes expired
    /// permissions and channel bindings, independent of client traffic.
    /// </summary>
    public uint AllocationSweepIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Whether requests must be authenticated using long-term credentials.
    /// </summary>
    public bool RequireAuthentication { get; init; } = true;

    /// <summary>
    /// Enables RFC 8016 mobility ticket processing.
    /// </summary>
    public bool EnableMobility { get; init; }

    /// <summary>
    /// How long a port reserved by an EVEN-PORT (reserve) allocation is held for the follow-up
    /// RESERVATION-TOKEN allocation before it is released (RFC 8656 §7). Default 30 s.
    /// </summary>
    public uint PortReservationLifetimeSeconds { get; init; } = 30;

    /// <summary>
    /// Maximum time a single TCP/TLS client may take to complete the TLS handshake before its connection
    /// is dropped. Without this bound a peer that dribbles or stalls the TLS ClientHello holds a
    /// connection slot indefinitely; the slot cap alone does not stop a slowloris, it only caps how many
    /// slots the attacker must hold to exhaust the server (K4). Applies to TLS transport only. Set to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> or zero to disable. Default 10 s.
    /// </summary>
    public TimeSpan StreamHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum time a single stream control-message read may take before the connection is dropped,
    /// bounding a slowloris that opens a connection and never delivers (or dribbles) a request (K4). The
    /// deadline is applied per frame and reset by every frame, so it must comfortably exceed a legitimate
    /// client's control cadence — chiefly the allocation refresh interval (≈ allocation lifetime / 2,
    /// RFC 8656 §3.9). The default matches <see cref="DefaultAllocationLifetimeSeconds"/> so a client
    /// refreshing on schedule never trips it; raise it if you grant much longer allocation lifetimes.
    /// Does not apply once a channel-bound relay takes over the connection. Set to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> or zero to disable.
    /// </summary>
    public TimeSpan StreamReadTimeout { get; init; } = TimeSpan.FromSeconds(600);
}
