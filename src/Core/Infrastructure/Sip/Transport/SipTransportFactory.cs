using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Default SIP transport factory creating the SDK's own multi-transport runtime.
/// </summary>
internal sealed class SipTransportFactory : ISipTransportFactory
{
    /// <inheritdoc />
    public ISipTransportRuntime Create(
        TlsConfiguration? tls,
        ILoggerFactory loggerFactory,
        SipTransportProtocol defaultTransport = SipTransportProtocol.Udp)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger<SipTransportFactory>();
        if (tls?.GetCertificate() is null && tls is not null)
        {
            logger.LogWarning(
                "TLS configuration is present but no certificate could be loaded; SIP TLS listener will remain disabled.");
        }

        return new SipTransportRuntime(
            loggerFactory,
            new SipWireProtocol(),
            tls,
            defaultTransport,
            routeResolver: null);
    }
}
