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
/// Options passed to offer/answer methods to include DTLS, ICE, rtcp-mux, and BUNDLE.
/// All fields are optional; omitted features are not emitted in the SDP.
/// </summary>
internal sealed class SdpMediaOptions
{
    /// <summary>DTLS-SRTP parameters; null = plain RTP or SDES.</summary>
    public SdpDtlsParameters? Dtls { get; init; }

    /// <summary>
    /// SDES crypto lines to advertise in an offer (RFC 4568). Empty = plain RTP/AVP;
    /// a non-empty list makes the offer emit one <c>a=crypto</c> per entry and the
    /// <c>RTP/SAVP</c> profile. Ignored on the answer path (answers key via the offer).
    /// </summary>
    public IReadOnlyList<SdpCryptoAttribute> Crypto { get; init; } = [];

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
