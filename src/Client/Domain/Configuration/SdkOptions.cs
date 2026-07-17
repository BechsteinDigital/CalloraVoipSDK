using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Security;

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

    /// <summary>
    /// Default SIP signaling transport for outbound requests. Defaults to
    /// <see cref="SipTransport.Udp"/>. See <see cref="SdkConfiguration.DefaultTransport"/>.
    /// </summary>
    public SipTransport DefaultTransport { get; set; } = SipTransport.Udp;

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

    /// <summary>
    /// Offer DTLS-SRTP keying (RFC 5763) on outbound media, not only SDES.
    /// See <see cref="SdkConfiguration.OfferDtlsSrtp"/> for semantics.
    /// </summary>
    public bool OfferDtlsSrtp { get; set; }

    /// <summary>
    /// Optional DTLS-SRTP identity certificate for the media plane; <see langword="null"/> uses the
    /// ephemeral ECDSA P-256 default. See <see cref="SdkConfiguration.DtlsCertificate"/> for semantics
    /// and constraints (ECDSA P-256 with an exportable private key).
    /// </summary>
    public X509Certificate2? DtlsCertificate { get; set; }

    /// <summary>
    /// Enable video (<c>m=video</c>) negotiation for calls.
    /// See <see cref="SdkConfiguration.EnableVideo"/> for semantics.
    /// </summary>
    public bool EnableVideo { get; set; }

    /// <summary>
    /// Ordered video codec preference by SDP encoding name; only applies when
    /// <see cref="EnableVideo"/> is set. <see langword="null"/> uses the SDK default.
    /// See <see cref="SdkConfiguration.PreferredVideoCodecs"/> for semantics.
    /// </summary>
    public IReadOnlyList<string>? PreferredVideoCodecs { get; set; }

    /// <summary>
    /// Audio format delivered to bridge/recording consumers.
    /// See <see cref="SdkConfiguration.BridgeAudioFormat"/> for semantics.
    /// </summary>
    public BridgeAudioFormat BridgeAudioFormat { get; set; } = BridgeAudioFormat.Passthrough;

    /// <summary>
    /// Timeout after which an answered call that never receives inbound media is torn down.
    /// See <see cref="SdkConfiguration.InboundMediaTimeout"/> for semantics.
    /// </summary>
    public TimeSpan InboundMediaTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Hang up a held call when its media goes silent.
    /// See <see cref="SdkConfiguration.HangupHeldCallOnMediaSilence"/> for semantics.
    /// </summary>
    public bool HangupHeldCallOnMediaSilence { get; set; }
}
