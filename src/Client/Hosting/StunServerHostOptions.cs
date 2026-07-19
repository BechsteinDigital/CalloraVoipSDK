using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// Mutable options bound from the DI container (Level 3), mapped onto the immutable
/// <see cref="StunServerHostConfiguration"/> when the host is resolved.
/// </summary>
public sealed class StunServerHostOptions
{
    /// <summary>See <see cref="StunServerHostConfiguration.BindEndPoint"/>. Defaults to all interfaces on port 3478.</summary>
    public IPEndPoint BindEndPoint { get; set; } = new(IPAddress.Any, 3478);

    /// <summary>See <see cref="StunServerHostConfiguration.Transport"/>.</summary>
    public IceTransport Transport { get; set; } = IceTransport.Udp;

    /// <summary>See <see cref="StunServerHostConfiguration.TlsCertificate"/>.</summary>
    public X509Certificate2? TlsCertificate { get; set; }

    /// <summary>See <see cref="StunServerHostConfiguration.LoggerFactory"/>.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Projects these mutable options onto the immutable host configuration.</summary>
    /// <param name="loggerFactory">The container's logger factory, used when none is set on the options.</param>
    public StunServerHostConfiguration ToConfiguration(ILoggerFactory? loggerFactory = null) => new()
    {
        BindEndPoint = BindEndPoint,
        Transport = Transport,
        TlsCertificate = TlsCertificate,
        LoggerFactory = LoggerFactory ?? loggerFactory,
    };
}
