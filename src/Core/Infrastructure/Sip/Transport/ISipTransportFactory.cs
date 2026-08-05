using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Factory contract for creating SIP signaling transports.
/// Enables dependency injection to swap transport implementations.
/// </summary>
internal interface ISipTransportFactory
{
    /// <summary>
    /// Creates one SIP transport runtime configured for the current SDK instance.
    /// </summary>
    /// <param name="tls">TLS settings for the secure listeners, or <see langword="null"/>.</param>
    /// <param name="loggerFactory">Logger factory for the runtime.</param>
    /// <param name="defaultTransport">
    /// Default transport for outbound requests and the advertised local endpoint when a target URI
    /// does not force one.
    /// </param>
    /// <param name="options">
    /// Inbound-listener admission and slowloris limits, or <see langword="null"/> to use the runtime defaults
    /// (#158 P1-3/P1-4).
    /// </param>
    ISipTransportRuntime Create(
        TlsConfiguration? tls,
        ILoggerFactory loggerFactory,
        SipTransportProtocol defaultTransport = SipTransportProtocol.Udp,
        SipTransportOptions? options = null);
}
