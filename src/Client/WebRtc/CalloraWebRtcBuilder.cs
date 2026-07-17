using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.WebRtc;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Builder for optional WebRTC-facade dependency overrides (Level 3). Mirrors <see cref="CalloraBuilder"/>
/// for the SIP facade and is returned by <see cref="WebRtcServiceCollectionExtensions.AddCalloraWebRtc"/>
/// and by <see cref="CalloraBuilder.AddWebRtc"/>.
/// </summary>
public sealed class CalloraWebRtcBuilder
{
    private readonly IServiceCollection _services;

    internal CalloraWebRtcBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Enables video negotiation and, when <paramref name="codecs"/> is non-empty, sets the ordered
    /// video codec preference (see <see cref="WebRtcOptions.VideoCodecs"/>).
    /// </summary>
    public CalloraWebRtcBuilder WithVideo(params string[] codecs)
    {
        _services.PostConfigure<WebRtcOptions>(options =>
        {
            options.EnableVideo = true;
            if (codecs is { Length: > 0 })
            {
                options.VideoCodecs = codecs;
            }
        });
        return this;
    }

    /// <summary>
    /// Pins the DTLS-SRTP identity certificate used for every peer (ECDSA P-256 with an exportable
    /// private key); see <see cref="WebRtcOptions.DtlsCertificate"/>.
    /// </summary>
    public CalloraWebRtcBuilder WithDtlsCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        _services.PostConfigure<WebRtcOptions>(options => options.DtlsCertificate = certificate);
        return this;
    }

    /// <summary>Overrides the logger factory used for WebRTC diagnostics.</summary>
    public CalloraWebRtcBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _services.PostConfigure<WebRtcOptions>(options => options.LoggerFactory = loggerFactory);
        return this;
    }
}
