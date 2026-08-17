using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Audio;
using CalloraVoipSdk.Core.Domain.Security;

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

    /// <summary>
    /// Local port the SIP listener binds for UDP and TCP. <c>0</c> (default) takes an ephemeral port.
    /// </summary>
    /// <remarks>
    /// Leave this at the default for registering accounts: the registrar learns where to reach you from the
    /// REGISTER Contact, so the port need not be fixed or known in advance.
    /// <para>
    /// Set it — normally to <c>5060</c> — when nobody tells the peer your address: an IP-authenticated trunk
    /// (<see cref="Core.Domain.Lines.SipAccount.Register"/> = <see langword="false"/>) sends no REGISTER, so
    /// the provider delivers inbound calls to a pre-agreed address. A fixed port is equally required for
    /// static firewall or NAT rules, which an ephemeral port would invalidate on every restart.
    /// </para>
    /// <para>
    /// Binding a port already in use fails at client construction with a <see cref="System.Net.Sockets.SocketException"/>
    /// rather than silently landing elsewhere — a listener on the wrong port looks healthy while every
    /// inbound call goes missing.
    /// </para>
    /// </remarks>
    public int            LocalSipPort           { get; init; }

    /// <summary>
    /// Local port the SIP TLS listener binds. <c>0</c> (default) takes an ephemeral port; the SIP convention
    /// is <c>5061</c> beside <c>5060</c> (RFC 3261 §19.1.2). Separate from <see cref="LocalSipPort"/> because
    /// TLS is a second TCP listener and cannot share that port. Only relevant when TLS is configured.
    /// </summary>
    public int            LocalSipTlsPort        { get; init; }

    /// <summary>Logger factory the SDK logs through; <see langword="null"/> disables SDK logging.</summary>
    public ILoggerFactory? LoggerFactory         { get; init; }

    /// <summary>
    /// Legacy advanced dependency provider for replacing internal runtime services.
    /// Prefer <c>AddCalloraVoip(...)</c> with <see cref="DependencyInjection.CalloraBuilder"/> overrides.
    /// </summary>
    [Obsolete("Use AddCalloraVoip(...)/CalloraBuilder overrides. VoipConfiguration.Services has been deprecated since v1.0 and is kept for backward compatibility; it may be removed in a future major release.", false)]
    public IServiceProvider? Services            { get; init; }
    /// <summary>
    /// Default media-encryption policy for calls; defaults to <see cref="SrtpPolicy.Optional"/>.
    /// Overridable per call via <c>DialOptions.UseSrtp</c>.
    /// <para>
    /// <b>Security note (RFC 4568 §7):</b> unless <see cref="OfferDtlsSrtp"/> is set, SRTP is keyed
    /// via SDES, which carries the master key as an <c>a=crypto</c> line inside the SDP. That key is
    /// only confidential when the signaling transport is secure (TLS/SIPS). Over UDP/TCP the key
    /// travels in cleartext, so a passive eavesdropper on the signaling path can decrypt the media —
    /// the SDK logs a warning in this case. For real media confidentiality use TLS/SIPS signaling or
    /// enable <see cref="OfferDtlsSrtp"/> (keys never appear in the SDP).
    /// </para>
    /// </summary>
    public SrtpPolicy     SrtpPolicy             { get; init; } = SrtpPolicy.Optional;

    /// <summary>
    /// When <see langword="true"/>, outbound call offers advertise DTLS-SRTP keying
    /// (RFC 5763: <c>UDP/TLS/RTP/SAVPF</c> profile plus certificate fingerprint) instead
    /// of SDES <c>a=crypto</c>. Inbound DTLS-SRTP offers are answered regardless of this
    /// setting. Default: <see langword="false"/> (SDES per <see cref="SrtpPolicy"/>).
    /// </summary>
    public bool           OfferDtlsSrtp          { get; init; }

    /// <summary>
    /// Opt-in hard enforcement of the RFC 4568 §7 caveat (see <see cref="SrtpPolicy"/>). When
    /// <see langword="true"/>, an outbound call that would key SDES over an insecure signaling transport
    /// (no TLS/SIPS, and <see cref="OfferDtlsSrtp"/> unset) is <b>refused</b> (fail-closed) instead of
    /// placed with the master key in cleartext SDP. Default: <see langword="false"/> — such calls proceed
    /// but the SDK logs a warning. Set this when a non-confidential media key must never leave the host;
    /// use TLS/SIPS signaling or DTLS-SRTP to place calls under this policy.
    /// </summary>
    public bool           RequireSecureSignalingForSdes { get; init; }

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
    /// Admission and slowloris limits for the inbound SIP listener (connection-oriented transports). Bounds
    /// how many inbound connections a peer can pin and how long a handshake may stall (#158 P1-3/P1-4). The
    /// defaults match the SDK's built-in limits.
    /// </summary>
    public SipTransportHardeningConfiguration SipTransportHardening { get; init; } = new();

    /// <summary>
    /// Resource limits for the SIP signaling layer: concurrent inbound session caps (global and per-remote),
    /// the un-answered ring deadline, and the inbound server-transaction table bounds (#158 P1-5/P1-7). The
    /// defaults match the SDK's built-in limits.
    /// </summary>
    public SipSignalingHardeningConfiguration SipSignalingHardening { get; init; } = new();

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
    /// Hang up a connected call that has shown no sign of life this long — neither inbound RTP nor
    /// inbound RTCP. The NAT-safe fallback for when a far-end BYE never reaches our in-dialog Contact
    /// and everything simply stops. <see cref="TimeSpan.Zero"/> disables the hangup.
    /// Default: 30 seconds.
    /// </summary>
    /// <remarks>
    /// Media silence alone does not end a call (#261): a peer using silence suppression (RFC 3389), a peer on
    /// hold, and a peer mid-bridge-switch during a transfer all stop sending media while continuing to report
    /// RTCP. Those are surfaced through <c>ICall.MediaFlowChanged</c> after
    /// <see cref="MediaSilenceNotifyAfter"/> instead, and the application decides what they mean. Only a peer
    /// that stops sending everything is treated as gone.
    /// </remarks>
    public TimeSpan InboundMediaTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Report inbound media silence through <c>ICall.MediaFlowChanged</c> after this long without inbound
    /// RTP — a notification, never a teardown. <see cref="TimeSpan.Zero"/> disables the notification.
    /// Default: 15 seconds.
    /// </summary>
    public TimeSpan MediaSilenceNotifyAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether the liveness timeout also applies to on-hold calls (which legitimately carry no inbound
    /// media). Default: <see langword="false"/> (held calls are not torn down).
    /// </summary>
    public bool HangupHeldCallOnMediaSilence { get; init; }
}
