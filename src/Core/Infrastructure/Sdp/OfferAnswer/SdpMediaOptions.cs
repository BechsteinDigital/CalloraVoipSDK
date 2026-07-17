using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// DTLS-SRTP parameters to include in an SDP offer or answer (RFC 5763 / RFC 8122 / RFC 4145).
/// </summary>
internal sealed class SdpDtlsParameters
{
    /// <summary>Hash algorithm, e.g. <c>sha-256</c>.</summary>
    public required string Algorithm { get; init; }

    /// <summary>Hex-encoded certificate fingerprint, colon-delimited, e.g. <c>AA:BB:CC:…</c>.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// DTLS setup role for the local endpoint (RFC 4145 §4).
    /// UAC SHOULD use <c>actpass</c> in offers; UAS answers with <c>active</c> or <c>passive</c>.
    /// Defaults to <c>actpass</c>.
    /// </summary>
    public string Setup { get; init; } = "actpass";
}

/// <summary>
/// ICE credentials and optional candidates to include in an SDP offer or answer (RFC 8839).
/// </summary>
internal sealed class SdpIceParameters
{
    /// <summary>ICE username fragment (<c>a=ice-ufrag</c>).</summary>
    public required string Ufrag { get; init; }

    /// <summary>ICE password (<c>a=ice-pwd</c>).</summary>
    public required string Pwd { get; init; }

    /// <summary>ICE candidates to include in the SDP (<c>a=candidate</c>).</summary>
    public IReadOnlyList<SdpIceCandidate> Candidates { get; init; } = [];

    /// <summary>Optional <c>a=ice-options</c> value, e.g. <c>trickle</c>.</summary>
    public string? Options { get; init; }
}

/// <summary>
/// Video media parameters for SDP offer/answer generation: the local RTP port for the
/// <c>m=video</c> line and the codec capabilities to offer/accept.
/// </summary>
internal sealed class SdpVideoMediaOptions
{
    /// <summary>Local UDP port advertised for video RTP.</summary>
    public required int Port { get; init; }

    /// <summary>Video codec capabilities (e.g. VP8/H264 at 90 kHz).</summary>
    public required IReadOnlyList<SdpCodecDefinition> Codecs { get; init; }

    /// <summary>
    /// Per-m-line SDES crypto lines for the video stream (RFC 4568), independent of audio's;
    /// empty for a plain or DTLS-keyed offer.
    /// </summary>
    public IReadOnlyList<SdpCryptoAttribute> Crypto { get; init; } = [];

    /// <summary>
    /// WebRTC MediaStream/track identity for the video m-line (<c>a=msid</c>, RFC 8830); null emits
    /// no <c>a=msid</c>.
    /// </summary>
    public SdpMsid? Msid { get; init; }

    /// <summary>
    /// Per-m-line ICE candidates for the video stream (<c>a=candidate</c>, RFC 8839); empty
    /// leaves the video m-line without its own candidates.
    /// </summary>
    public IReadOnlyList<SdpIceCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// RTP header-extension URIs the SDK supports/offers on the video m-line (RFC 8285). The
    /// negotiator assigns one-byte ids in an offer and echoes the offered ids in an answer.
    /// </summary>
    public IReadOnlyList<string> HeaderExtensionUris { get; init; } = [];
}

/// <summary>
/// Options passed to offer/answer methods to include DTLS, ICE, rtcp-mux, and BUNDLE.
/// All fields are optional; omitted features are not emitted in the SDP.
/// </summary>
internal sealed class SdpMediaOptions
{
    /// <summary>DTLS-SRTP parameters; null = plain RTP or SDES.</summary>
    public SdpDtlsParameters? Dtls { get; init; }

    /// <summary>
    /// Video media to offer/answer; null answers video m-lines with a zero-port mirror
    /// (RFC 3264 §6) and offers audio only.
    /// </summary>
    public SdpVideoMediaOptions? Video { get; init; }

    /// <summary>
    /// SDES crypto lines to advertise in an offer (RFC 4568). Empty = plain RTP/AVP;
    /// a non-empty list makes the offer emit one <c>a=crypto</c> per entry and the
    /// <c>RTP/SAVP</c> profile. Ignored on the answer path (answers key via the offer).
    /// </summary>
    public IReadOnlyList<SdpCryptoAttribute> Crypto { get; init; } = [];

    /// <summary>
    /// WebRTC MediaStream/track identity for the audio m-line (<c>a=msid</c>, RFC 8830); null emits
    /// no <c>a=msid</c>.
    /// </summary>
    public SdpMsid? AudioMsid { get; init; }

    /// <summary>ICE credentials and candidates; null = no ICE.</summary>
    public SdpIceParameters? Ice { get; init; }

    /// <summary>Whether to include <c>a=rtcp-mux</c> (RFC 5761).</summary>
    public bool RtcpMux { get; init; }

    /// <summary>Whether to add BUNDLE grouping and <c>a=mid:audio</c> (RFC 5888).</summary>
    public bool Bundle { get; init; }

    /// <summary>Origin session id for the built SDP (<c>o=</c> sess-id, RFC 4566 §5.2).</summary>
    public long SessionId { get; init; }

    /// <summary>Origin session version for the built SDP (<c>o=</c> sess-version, RFC 4566 §5.2).</summary>
    public long SessionVersion { get; init; }
}
