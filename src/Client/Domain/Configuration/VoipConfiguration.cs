using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Infrastructure.Audio;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Security;

namespace CalloraVoipSdk;

/// <summary>
/// Top-level SDK configuration: identity, transport security, logging, media/codec behavior, and
/// call-lifecycle timeouts. Supplied once when constructing or registering the SDK.
/// </summary>
public sealed class VoipConfiguration
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
    /// Prefer <c>AddCalloraVoip(...)</c> with <see cref="DependencyInjection.CalloraBuilder"/> overrides.
    /// </summary>
    [Obsolete("Use AddCalloraVoip(...)/CalloraBuilder overrides. VoipConfiguration.Services will be removed after v1.0.", false)]
    public IServiceProvider? Services            { get; init; }
    /// <summary>Default media-encryption policy for calls; defaults to <see cref="SrtpPolicy.Optional"/>. Overridable per call via <c>DialOptions.UseSrtp</c>.</summary>
    public SrtpPolicy     SrtpPolicy             { get; init; } = SrtpPolicy.Optional;

    /// <summary>
    /// When <see langword="true"/>, outbound call offers advertise DTLS-SRTP keying
    /// (RFC 5763: <c>UDP/TLS/RTP/SAVPF</c> profile plus certificate fingerprint) instead
    /// of SDES <c>a=crypto</c>. Inbound DTLS-SRTP offers are answered regardless of this
    /// setting. Default: <see langword="false"/> (SDES per <see cref="SrtpPolicy"/>).
    /// </summary>
    public bool           OfferDtlsSrtp          { get; init; }

    /// <summary>
    /// Optional DTLS-SRTP identity certificate (RFC 5763) for the media plane. <see langword="null"/>
    /// (default) generates a fresh ephemeral ECDSA P-256 certificate per client instance — the WebRTC
    /// privacy default. Supply your own for a stable/pinned identity (enterprise, compliance): it must be
    /// an ECDSA <b>P-256</b> certificate with an accessible private key (RSA, other curves, and
    /// non-exportable HSM keys are rejected fail-closed). The DTLS certificate is authenticated by SDP
    /// <c>a=fingerprint</c> (RFC 8122), not PKI, and is independent of the SIP-TLS certificate
    /// (<see cref="Tls"/>) — pass the same <see cref="X509Certificate2"/> to both to share one identity.
    /// </summary>
    public X509Certificate2? DtlsCertificate    { get; init; }

    /// <summary>
    /// When <see langword="true"/>, calls negotiate a video stream (WebRTC phase 2,
    /// RFC 6184/7741): offers carry an <c>m=video</c> line and inbound video offers are
    /// answered. Encoded video frames are exchanged via <c>ICall</c>'s media session;
    /// the SDK does not encode/decode video itself. Default: <see langword="false"/>
    /// (audio-only). Video is offered only when the offer is not SDES-keyed: an outbound
    /// offer under a SDES-offering <see cref="SrtpPolicy"/> (Optional/Required without
    /// <see cref="OfferDtlsSrtp"/>) stays audio-only — offer DTLS-SRTP or run plain to
    /// carry video. Inbound plain/DTLS video offers are answered regardless; SDES video
    /// is declined until per-m-line video keying lands.
    /// Note: video does not yet gather ICE candidates — with <see cref="Ice"/> enabled the
    /// video m-line carries its port but no candidates, so video needs direct connectivity
    /// (no ICE-only peer) until per-component video ICE lands.
    /// </summary>
    public bool           EnableVideo            { get; init; }

    /// <summary>
    /// Ordered video codec preference by SDP encoding name (<c>VP8</c>, <c>H264</c>) when
    /// <see cref="EnableVideo"/> is set. <see langword="null"/> uses the SDK default
    /// (VP8, then H264). Unknown names are ignored.
    /// </summary>
    public IReadOnlyList<string>? PreferredVideoCodecs { get; init; }

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
