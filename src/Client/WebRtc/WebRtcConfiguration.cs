using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Immutable configuration for a <see cref="WebRtcClient"/> (the direct-construction surface; the
/// DI/options path adds a mutable counterpart in a later slice). All fields are optional — a
/// zero-config <c>new WebRtcClient()</c> binds an ephemeral loopback endpoint, offers Opus audio, and
/// uses a fresh per-peer DTLS identity.
/// </summary>
public sealed class WebRtcConfiguration
{
    /// <summary>
    /// Local media endpoint the peer binds for RTP/RTCP/ICE/DTLS. Default is an ephemeral loopback
    /// port; production deployments set a reachable address. (Host-candidate advertisement and trickle
    /// ICE for remote reachability arrive in a later slice — see ADR-012.)
    /// </summary>
    public IPEndPoint LocalEndPoint { get; init; } = new(IPAddress.Loopback, 0);

    /// <summary>Audio codecs to offer, by name (<c>opus</c>, <c>PCMU</c>, <c>PCMA</c>, <c>G722</c>). Default: Opus.</summary>
    public IReadOnlyList<string> AudioCodecs { get; init; } = ["opus"];

    /// <summary>Whether to offer a video m-line.</summary>
    public bool EnableVideo { get; init; }

    /// <summary>Video codecs to offer when <see cref="EnableVideo"/> is set, by name (<c>H264</c>, <c>VP8</c>). Default: H264.</summary>
    public IReadOnlyList<string> VideoCodecs { get; init; } = ["H264"];

    /// <summary>
    /// DTLS-SRTP identity for the peer's certificate/fingerprint (must carry an exportable ECDSA P-256
    /// private key); <see langword="null"/> generates a fresh ephemeral identity per peer — the WebRTC
    /// privacy default.
    /// </summary>
    public X509Certificate2? DtlsCertificate { get; init; }

    /// <summary>Logger factory for diagnostics; <see langword="null"/> disables logging.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
