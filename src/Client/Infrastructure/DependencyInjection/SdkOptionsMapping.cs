using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Audio;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Projects the host-facing <see cref="SdkOptions"/> onto the runtime <see cref="SdkConfiguration"/>.
/// Kept a pure function so the option-to-configuration mapping is unit-testable independently of the
/// DI container.
/// </summary>
internal static class SdkOptionsMapping
{
    /// <summary>
    /// Builds the <see cref="SdkConfiguration"/> that backs the client from the configured options,
    /// using <paramref name="loggerFactory"/> as the resolved logger factory. Every configurable
    /// option is carried across; unset options fall through to the configuration's own defaults.
    /// </summary>
    public static SdkConfiguration ToConfiguration(this SdkOptions options, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new SdkConfiguration
        {
            UserAgent = options.UserAgent,
            Tls = options.Tls,
            DefaultTransport = options.DefaultTransport,
            LoggerFactory = loggerFactory,
            SrtpPolicy = options.SrtpPolicy,
            Ice = options.Ice.ToConfiguration(),
            MaxConcurrentCallsPerLine = options.MaxConcurrentCallsPerLine,
            AudioDevice = options.AudioDevice ?? SilenceAudioDevice.Instance,
            EnableAutomaticAudioDeviceSelection = options.EnableAutomaticAudioDeviceSelection,
            PreferredAudioCodecs = options.PreferredAudioCodecs,
            OfferDtlsSrtp = options.OfferDtlsSrtp,
            DtlsCertificate = options.DtlsCertificate,
            EnableVideo = options.EnableVideo,
            PreferredVideoCodecs = options.PreferredVideoCodecs,
            BridgeAudioFormat = options.BridgeAudioFormat,
            InboundMediaTimeout = options.InboundMediaTimeout,
            HangupHeldCallOnMediaSilence = options.HangupHeldCallOnMediaSilence
        };
    }
}
