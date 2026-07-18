using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Host-facing, mutable options for the WebRTC facade, consumed by <c>AddCalloraWebRtc(...)</c> — the
/// dependency-injection counterpart to the immutable <see cref="WebRtcConfiguration"/> (mirrors how
/// <c>VoipOptions</c> backs <c>VoipConfiguration</c> for the SIP facade).
/// </summary>
/// <remarks>
/// This is the WebRTC facade's <em>own</em> option surface: a sibling of the SIP <c>VoipOptions</c>, not a
/// section nested inside it. A pure-WebRTC host therefore never carries SIP settings, and the two facades
/// meet only at the composition layer (<c>AddCalloraVoip(...).AddWebRtc(...)</c>). See ADR-012.
/// </remarks>
public sealed class WebRtcOptions
{
    /// <summary>Local media endpoint the peer binds. See <see cref="WebRtcConfiguration.LocalEndPoint"/>.</summary>
    public IPEndPoint LocalEndPoint { get; set; } = new(IPAddress.Loopback, 0);

    /// <summary>Audio codecs to offer, by name. See <see cref="WebRtcConfiguration.AudioCodecs"/>. Default: Opus.</summary>
    public IReadOnlyList<string> AudioCodecs { get; set; } = ["opus"];

    /// <summary>Whether to offer a video m-line. See <see cref="WebRtcConfiguration.EnableVideo"/>.</summary>
    public bool EnableVideo { get; set; }

    /// <summary>Video codecs to offer when <see cref="EnableVideo"/> is set. See <see cref="WebRtcConfiguration.VideoCodecs"/>. Default: H264.</summary>
    public IReadOnlyList<string> VideoCodecs { get; set; } = ["H264"];

    /// <summary>
    /// DTLS-SRTP identity certificate for the peer (ECDSA P-256 with an exportable private key);
    /// <see langword="null"/> generates a fresh per-peer identity. See <see cref="WebRtcConfiguration.DtlsCertificate"/>.
    /// </summary>
    public X509Certificate2? DtlsCertificate { get; set; }

    /// <summary>
    /// Logger factory override; <see langword="null"/> falls back to the container's registered factory.
    /// See <see cref="WebRtcConfiguration.LoggerFactory"/>.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }
}
