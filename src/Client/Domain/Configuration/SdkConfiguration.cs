using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Infrastructure.Audio;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk;

/// <summary>
/// Top-level SDK configuration: identity, transport security, logging, media/codec behavior, and
/// call-lifecycle timeouts. Supplied once when constructing or registering the SDK.
/// </summary>
public sealed class SdkConfiguration
{
    /// <summary>Value sent in the SIP <c>User-Agent</c> header; defaults to <c>CalloraVoipSdk/1.0</c>.</summary>
    public string         UserAgent              { get; init; } = "CalloraVoipSdk/1.0";

    /// <summary>TLS settings for secure SIP transport; <see langword="null"/> uses the transport defaults.</summary>
    public TlsConfiguration? Tls                { get; init; }

    /// <summary>
    /// Default SIP signaling transport for outbound requests and the advertised local contact when
    /// a target URI does not force one. Defaults to <see cref="SipTransport.Udp"/>, preserving the
    /// prior behavior; set to <see cref="SipTransport.Tcp"/>/<see cref="SipTransport.Tls"/>/etc. for
    /// TCP- or TLS-only enterprise proxies.
    /// </summary>
    public SipTransport   DefaultTransport       { get; init; } = SipTransport.Udp;

    /// <summary>Logger factory the SDK logs through; <see langword="null"/> disables SDK logging.</summary>
    public ILoggerFactory? LoggerFactory         { get; init; }

    /// <summary>
    /// Legacy advanced dependency provider for replacing internal runtime services.
    /// Prefer <c>AddCallora(...)</c> with <see cref="DependencyInjection.CalloraBuilder"/> overrides.
    /// </summary>
    [Obsolete("Use AddCallora(...)/CalloraBuilder overrides. SdkConfiguration.Services will be removed after v1.0.", false)]
    public IServiceProvider? Services            { get; init; }
    /// <summary>Default media-encryption policy for calls; defaults to <see cref="SrtpPolicy.Optional"/>. Overridable per call via <c>DialOptions.UseSrtp</c>.</summary>
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
    /// Ordered audio codec preference by SDP encoding name ("PCMU", "PCMA", "G722",
    /// "opus"). When set, SDP offers and answers only include the listed codecs (plus
    /// DTMF telephone-event) in this order, and RTP sessions use this preference to pick
    /// the primary codec. Opus (RFC 7587, 48 kHz) is opt-in: it is only offered/answered
    /// when listed here. Unknown names are ignored; when nothing matches, the SDK default
    /// set (G722, PCMA, PCMU) is used. When a listed codec is known but the peer does not
    /// offer it, negotiation fails rather than producing an audio-less answer.
    /// <see langword="null"/> keeps defaults.
    /// </summary>
    public IReadOnlyList<string>? PreferredAudioCodecs { get; init; }

    /// <summary>
    /// Audio format delivered to and expected from the media consumer (bridge/tap). When set
    /// to <see cref="BridgeAudioFormat.Pcmu"/>, the SDK transcodes between the negotiated wire
    /// codec (e.g. Opus) and G.711 µ-law so a µ-law-only consumer works over any negotiated
    /// codec. Default <see cref="BridgeAudioFormat.Passthrough"/> delivers the raw wire payload.
    /// </summary>
    public BridgeAudioFormat BridgeAudioFormat { get; init; } = BridgeAudioFormat.Passthrough;

    /// <summary>
    /// Hang up a connected call whose inbound RTP has been silent this long — a NAT-safe
    /// fallback for when a far-end BYE never reaches our in-dialog Contact and the media just
    /// stops. <see cref="TimeSpan.Zero"/> disables the check. Default: 15 seconds.
    /// </summary>
    public TimeSpan InboundMediaTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether the inbound-media timeout also applies to on-hold calls (which legitimately
    /// carry no inbound RTP). Default: <see langword="false"/> (held calls are not torn down).
    /// </summary>
    public bool HangupHeldCallOnMediaSilence { get; init; }
}
