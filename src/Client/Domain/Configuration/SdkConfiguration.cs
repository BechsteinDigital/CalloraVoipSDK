using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Infrastructure.Audio;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk;

public sealed class SdkConfiguration
{
    public string         UserAgent              { get; init; } = "CalloraVoipSdk/1.0";
    public TlsConfiguration? Tls                { get; init; }
    public ILoggerFactory? LoggerFactory         { get; init; }

    /// <summary>
    /// Legacy advanced dependency provider for replacing internal runtime services.
    /// Prefer <c>AddCallora(...)</c> with <see cref="DependencyInjection.CalloraBuilder"/> overrides.
    /// </summary>
    [Obsolete("Use AddCallora(...)/CalloraBuilder overrides. SdkConfiguration.Services will be removed after v1.0.", false)]
    public IServiceProvider? Services            { get; init; }
    public SrtpPolicy     SrtpPolicy             { get; init; } = SrtpPolicy.Optional;

    /// <summary>
    /// ICE runtime configuration for NAT traversal and candidate-pair selection.
    /// Disabled by default.
    /// </summary>
    public IceConfiguration Ice { get; init; } = new();

    /// <summary>
    /// Maximum simultaneous calls per phone line. 0 = unlimited.
    /// </summary>
    public int MaxConcurrentCallsPerLine { get; init; } = 10;

    /// <summary>
    /// Audio device to use for all calls.
    /// If left at SilenceAudioDevice and auto selection is enabled, the SDK
    /// attempts to load a platform device (Linux/Windows) at runtime.
    /// </summary>
    public IAudioDevice AudioDevice { get; init; } = SilenceAudioDevice.Instance;

    /// <summary>
    /// Automatically load a platform audio device when <see cref="AudioDevice"/>
    /// is left at <see cref="SilenceAudioDevice"/>.
    /// </summary>
    public bool EnableAutomaticAudioDeviceSelection { get; init; } = true;

    /// <summary>
    /// Ordered audio codec preference by SDP encoding name ("PCMU", "PCMA", "G722").
    /// When set, SDP offers and answers only include the listed codecs (plus DTMF
    /// telephone-event) in this order, and RTP sessions use this preference to pick the
    /// primary codec. Unsupported names are ignored; when nothing matches, the SDK
    /// default set (G722, PCMA, PCMU) is used. <see langword="null"/> keeps defaults.
    /// </summary>
    public IReadOnlyList<string>? PreferredAudioCodecs { get; init; }
}
