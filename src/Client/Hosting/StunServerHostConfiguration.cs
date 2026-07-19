using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// Immutable configuration a <see cref="StunServerHost"/> is built from (Level 2). A STUN Binding server needs
/// no credentials; it just returns the client's server-reflexive (mapped) address.
/// </summary>
public sealed class StunServerHostConfiguration
{
    /// <summary>The endpoint the server binds to. Use port <c>0</c> for an ephemeral port (read back via LocalEndPoint).</summary>
    public required IPEndPoint BindEndPoint { get; init; }

    /// <summary>The transport the server listens on (UDP, TCP, or TLS). TLS requires <see cref="TlsCertificate"/>.</summary>
    public IceTransport Transport { get; init; } = IceTransport.Udp;

    /// <summary>The server certificate for a TLS transport; ignored for UDP/TCP. Required when <see cref="Transport"/> is TLS.</summary>
    public X509Certificate2? TlsCertificate { get; init; }

    /// <summary>The logger factory for server diagnostics; a no-op factory is used when null.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
