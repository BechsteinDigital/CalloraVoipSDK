using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// Mutable options bound from the DI container (Level 3), mapped onto the immutable
/// <see cref="TurnServerHostConfiguration"/> when the host is resolved. Mirrors <c>WebRtcOptions</c>.
/// </summary>
public sealed class TurnServerHostOptions
{
    /// <summary>See <see cref="TurnServerHostConfiguration.BindEndPoint"/>. Defaults to all interfaces on port 3478.</summary>
    public IPEndPoint BindEndPoint { get; set; } = new(IPAddress.Any, 3478);

    /// <summary>See <see cref="TurnServerHostConfiguration.Transport"/>.</summary>
    public IceTransport Transport { get; set; } = IceTransport.Udp;

    /// <summary>See <see cref="TurnServerHostConfiguration.RequireAuthentication"/>.</summary>
    public bool RequireAuthentication { get; set; } = true;

    /// <summary>See <see cref="TurnServerHostConfiguration.Realm"/>.</summary>
    public string? Realm { get; set; }

    /// <summary>The long-term credentials the server accepts; add via the builder or directly.</summary>
    public IList<TurnServerCredential> Credentials { get; } = [];

    /// <summary>See <see cref="TurnServerHostConfiguration.TlsCertificate"/>.</summary>
    public X509Certificate2? TlsCertificate { get; set; }

    /// <summary>See <see cref="TurnServerHostConfiguration.DefaultAllocationLifetimeSeconds"/>.</summary>
    public uint DefaultAllocationLifetimeSeconds { get; set; } = 600;

    /// <summary>See <see cref="TurnServerHostConfiguration.MaxAllocationLifetimeSeconds"/>.</summary>
    public uint MaxAllocationLifetimeSeconds { get; set; } = 3600;

    /// <summary>See <see cref="TurnServerHostConfiguration.MaxTotalAllocations"/>.</summary>
    public int MaxTotalAllocations { get; set; } = 16384;

    /// <summary>See <see cref="TurnServerHostConfiguration.LoggerFactory"/>.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Projects these mutable options onto the immutable host configuration.</summary>
    /// <param name="loggerFactory">The container's logger factory, used when none is set on the options.</param>
    public TurnServerHostConfiguration ToConfiguration(ILoggerFactory? loggerFactory = null) => new()
    {
        BindEndPoint = BindEndPoint,
        Transport = Transport,
        RequireAuthentication = RequireAuthentication,
        Realm = Realm,
        Credentials = [.. Credentials],
        TlsCertificate = TlsCertificate,
        DefaultAllocationLifetimeSeconds = DefaultAllocationLifetimeSeconds,
        MaxAllocationLifetimeSeconds = MaxAllocationLifetimeSeconds,
        MaxTotalAllocations = MaxTotalAllocations,
        LoggerFactory = LoggerFactory ?? loggerFactory,
    };
}
