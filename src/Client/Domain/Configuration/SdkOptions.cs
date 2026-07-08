using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk;

/// <summary>
/// Options model used by <c>AddCallora(...)</c> for host-based configuration.
/// </summary>
public sealed class SdkOptions
{
    /// <summary>User agent used in SIP requests.</summary>
    public string UserAgent { get; set; } = "CalloraVoipSdk/1.0";

    /// <summary>Optional TLS runtime settings.</summary>
    public TlsConfiguration? Tls { get; set; }

    /// <summary>SRTP negotiation policy.</summary>
    public SrtpPolicy SrtpPolicy { get; set; } = SrtpPolicy.Optional;

    /// <summary>ICE runtime configuration options.</summary>
    public IceOptions Ice { get; set; } = new();

    /// <summary>Maximum simultaneous calls per line. 0 = unlimited.</summary>
    public int MaxConcurrentCallsPerLine { get; set; } = 10;

    /// <summary>
    /// Optional explicit audio device instance.
    /// </summary>
    public IAudioDevice? AudioDevice { get; set; }

    /// <summary>
    /// Automatically load a platform audio device when <see cref="AudioDevice"/> is not set.
    /// </summary>
    public bool EnableAutomaticAudioDeviceSelection { get; set; } = true;

    /// <summary>
    /// Optional logger factory override.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Ordered audio codec preference by SDP encoding name ("PCMU", "PCMA", "G722").
    /// See <see cref="SdkConfiguration.PreferredAudioCodecs"/> for semantics.
    /// </summary>
    public IReadOnlyList<string>? PreferredAudioCodecs { get; set; }
}
