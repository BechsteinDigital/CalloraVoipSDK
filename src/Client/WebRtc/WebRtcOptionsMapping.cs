using Microsoft.Extensions.Logging;
using CalloraVoipSdk.WebRtc;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Projects the host-facing <see cref="WebRtcOptions"/> onto the immutable <see cref="WebRtcConfiguration"/>.
/// Kept a pure function so the option-to-configuration mapping is unit-testable independently of the DI
/// container (mirrors <c>VoipOptionsMapping</c> for the SIP facade).
/// </summary>
internal static class WebRtcOptionsMapping
{
    /// <summary>
    /// Builds the <see cref="WebRtcConfiguration"/> that backs the client from the configured options,
    /// using <paramref name="loggerFactory"/> as the resolved logger factory when the options do not pin
    /// one. Every configurable option is carried across; unset options fall through to the
    /// configuration's own defaults.
    /// </summary>
    public static WebRtcConfiguration ToConfiguration(this WebRtcOptions options, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new WebRtcConfiguration
        {
            LocalEndPoint = options.LocalEndPoint,
            AudioCodecs = options.AudioCodecs,
            EnableVideo = options.EnableVideo,
            VideoCodecs = options.VideoCodecs,
            SimulcastLayers = options.SimulcastLayers,
            IceServers = options.IceServers,
            DtlsCertificate = options.DtlsCertificate,
            LoggerFactory = options.LoggerFactory ?? loggerFactory,
        };
    }
}
