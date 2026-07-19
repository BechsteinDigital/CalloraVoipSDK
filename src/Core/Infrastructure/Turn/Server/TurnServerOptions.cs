namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Runtime limits and behavior for the TURN server.
/// </summary>
internal sealed class TurnServerOptions
{
    /// <summary>Default options instance.</summary>
    public static TurnServerOptions Default { get; } = new();

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
}
