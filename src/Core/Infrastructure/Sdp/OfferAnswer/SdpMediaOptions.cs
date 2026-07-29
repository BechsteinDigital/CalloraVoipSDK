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
    /// Per-m-line ICE candidates for the video stream (<c>a=candidate</c>, RFC 8839); empty
    /// leaves the video m-line without its own candidates.
    /// </summary>
    public IReadOnlyList<SdpIceCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// RTP header-extension URIs the SDK supports/offers on the video m-line (RFC 8285). The
    /// negotiator assigns one-byte ids in an offer and echoes the offered ids in an answer.
    /// </summary>
    public IReadOnlyList<string> HeaderExtensionUris { get; init; } = [];

    /// <summary>
    /// Send-side simulcast layer ids (RFC 8853) to advertise on the video m-line as <c>a=rid … send</c>
    /// plus <c>a=simulcast:send …</c>. Empty offers a single video stream. When non-empty, the offer also
    /// carries the RID header extension (RFC 8852) so each layer's SSRC is per-packet identifiable.
    /// </summary>
    public IReadOnlyList<string> SimulcastSendRids { get; init; } = [];
}

/// <summary>
/// One media track to offer as its own m-line on the shared BUNDLE transport (RFC 8843 / RFC 8829).
/// Feeds the multi-track offer path: a caller supplies a <see cref="SdpMediaOptions.Tracks"/> list of these,
/// one per m-line, and the negotiator emits an m-line per track with a numeric <c>a=mid</c> by index
/// (0, 1, 2, …) and a <c>a=group:BUNDLE 0 1 …</c> — mirroring how libwebrtc/SIPSorcery build multi-track SDP.
/// A single-typed track carries the same per-m-line facts the fixed audio/video path does (codecs, msid,
/// per-m-line SDES crypto, header extensions, and — for video — send-side simulcast rids).
/// </summary>
internal sealed class SdpTrackOptions
{
    /// <summary>Media kind: <c>"audio"</c> or <c>"video"</c> (the m-line media type, RFC 4566 §5.14).</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The negotiated direction of this m-line (RFC 3264 §5.1). Defaults to <see cref="SdpMediaDirection.SendRecv"/>
    /// so a track list that does not set it emits the same <c>a=sendrecv</c> m-lines the pre-direction
    /// multi-track path did (byte-identity preserved).
    /// </summary>
    public SdpMediaDirection Direction { get; init; } = SdpMediaDirection.SendRecv;

    /// <summary>Codec capabilities to offer on this m-line (audio codecs, or VP8/H264 at 90 kHz for video).</summary>
    public required IReadOnlyList<SdpCodecDefinition> Codecs { get; init; }

    /// <summary>WebRTC MediaStream/track identity for this m-line (<c>a=msid</c>, RFC 8830); null emits none.</summary>
    public SdpMsid? Msid { get; init; }

    /// <summary>Per-m-line SDES crypto lines (RFC 4568), independent of other m-lines; empty for plain/DTLS keying.</summary>
    public IReadOnlyList<SdpCryptoAttribute> Crypto { get; init; } = [];

    /// <summary>RTP header-extension URIs supported/offered on this m-line (RFC 8285); ids assigned per offer.</summary>
    public IReadOnlyList<string> HeaderExtensionUris { get; init; } = [];

    /// <summary>Send-side simulcast layer ids (RFC 8853), video only; empty offers a single stream.</summary>
    public IReadOnlyList<string> SimulcastSendRids { get; init; } = [];
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
    /// Explicit multi-track m-line list for a WebRTC offer (RFC 8843 BUNDLE). When non-empty, the offer is
    /// built from this list — one m-line per entry, numeric <c>a=mid</c> by index, <c>a=group:BUNDLE 0 1 …</c> —
    /// instead of the fixed one-audio-plus-optional-video shape, and the <see cref="Video"/>/audio-codec inputs
    /// are ignored for m-line layout. <see langword="null"/> or empty (default) keeps the byte-identical
    /// single-audio (+ optional single-video) offer, so the SIP path and existing WebRTC 1+1 offers are unchanged.
    /// Answer-side multi-track negotiation is separate (a later slice).
    /// </summary>
    public IReadOnlyList<SdpTrackOptions>? Tracks { get; init; }

    /// <summary>
    /// SDES crypto lines to advertise in an offer (RFC 4568). Empty = plain RTP/AVP;
    /// a non-empty list makes the offer emit one <c>a=crypto</c> per entry and the
    /// <c>RTP/SAVP</c> profile. Ignored on the answer path (answers key via the offer).
    /// </summary>
    public IReadOnlyList<SdpCryptoAttribute> Crypto { get; init; } = [];

    /// <summary>
    /// WebRTC MediaStream/track identity for the audio m-line (<c>a=msid</c>, RFC 8830); null emits
    /// no <c>a=msid</c>. On a multi-track answer (RFC 8843) it is the fallback applied to every audio
    /// m-line that <see cref="AudioMsidByMid"/> does not name explicitly.
    /// </summary>
    public SdpMsid? AudioMsid { get; init; }

    /// <summary>
    /// Per-m-line WebRTC MediaStream/track identity for a multi-track audio answer (RFC 8830 / RFC 8843),
    /// keyed by the offered numeric <c>a=mid</c>. When answering an offer with N audio m-lines (an SFU
    /// forwarding N participants), each forwarded audio needs its own <c>a=msid</c>; this map supplies one
    /// per MID. A MID absent from the map falls back to <see cref="AudioMsid"/>, so the single-audio path —
    /// which supplies only <see cref="AudioMsid"/> — stays byte-identical. <see langword="null"/> (default)
    /// keeps every audio m-line on <see cref="AudioMsid"/>, unchanged from the pre-multi-track answer.
    /// </summary>
    public IReadOnlyDictionary<string, SdpMsid>? AudioMsidByMid { get; init; }

    /// <summary>
    /// WebRTC MediaStream/track identity for the video m-line (<c>a=msid</c>, RFC 8830); null emits
    /// no <c>a=msid</c>. Kept at the negotiation-options level (parallel to <see cref="AudioMsid"/>)
    /// so the caller need not clone the app-supplied <see cref="Video"/> options to add identity.
    /// </summary>
    public SdpMsid? VideoMsid { get; init; }

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
