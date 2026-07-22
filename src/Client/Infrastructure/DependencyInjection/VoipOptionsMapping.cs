using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Audio;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Projects the host-facing <see cref="VoipOptions"/> onto the runtime <see cref="VoipConfiguration"/>.
/// Kept a pure function so the option-to-configuration mapping is unit-testable independently of the
/// DI container.
/// </summary>
internal static class VoipOptionsMapping
{
    /// <summary>
    /// Builds the <see cref="VoipConfiguration"/> that backs the client from the configured options,
    /// using <paramref name="loggerFactory"/> as the resolved logger factory. Every configurable
    /// option is carried across; unset options fall through to the configuration's own defaults.
    /// </summary>
    public static VoipConfiguration ToConfiguration(this VoipOptions options, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new VoipConfiguration
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
            RequireSecureSignalingForSdes = options.RequireSecureSignalingForSdes,
            DtlsCertificate = options.DtlsCertificate,
            EnableVideo = options.EnableVideo,
            PreferredVideoCodecs = options.PreferredVideoCodecs,
            BridgeAudioFormat = options.BridgeAudioFormat,
            InboundMediaTimeout = options.InboundMediaTimeout,
            HangupHeldCallOnMediaSilence = options.HangupHeldCallOnMediaSilence
        };
    }
}
