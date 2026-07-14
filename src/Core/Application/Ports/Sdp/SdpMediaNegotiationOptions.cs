using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Ports.Sdp;

/// <summary>
/// ICE parameters for local SDP offer/answer generation.
/// </summary>
public sealed class SdpIceNegotiationOptions
{
    /// <summary>
    /// Local ICE username fragment.
    /// </summary>
    public required string Ufrag { get; init; }

    /// <summary>
    /// Local ICE password.
    /// </summary>
    public required string Pwd { get; init; }

    /// <summary>
    /// Local ICE candidates emitted into SDP.
    /// </summary>
    public IReadOnlyList<CallIceCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Optional ICE options value (for example trickle).
    /// </summary>
    public string? Options { get; init; }
}

/// <summary>
/// Local DTLS-SRTP identity for SDP offer/answer generation (RFC 5763 / RFC 8122):
/// the certificate fingerprint signaled via <c>a=fingerprint</c>. Kept as plain strings
/// so the application port stays free of crypto types.
/// </summary>
public sealed class SdpDtlsNegotiationOptions
{
    /// <summary>Fingerprint hash function token, e.g. <c>sha-256</c>.</summary>
    public required string FingerprintAlgorithm { get; init; }

    /// <summary>Colon-delimited hex fingerprint of the local DTLS certificate.</summary>
    public required string FingerprintValue { get; init; }
}

/// <summary>
/// Video parameters for SDP offer/answer generation (WebRTC phase 2): the local video
/// RTP port and the codec preference. Presence of this object enables answering video
/// m-lines and (on the offer path) offering an <c>m=video</c> line.
/// </summary>
public sealed class SdpVideoNegotiationOptions
{
    /// <summary>Local UDP port advertised for video RTP.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// Ordered video codec preference by SDP encoding name (<c>VP8</c>, <c>H264</c>).
    /// <see langword="null"/> uses the SDK default (VP8, H264). Unknown names are ignored.
    /// </summary>
    public IReadOnlyList<string>? PreferredCodecNames { get; init; }

    /// <summary>
    /// Live outbound SDES key params for the video m-line to re-advertise on a re-offer
    /// (hold/unhold), so a running SRTP video stream is not rekeyed mid-call. Applies only when
    /// the session offers SDES crypto; <see langword="null"/> generates a fresh video key.
    /// </summary>
    public string? OfferSrtpKeyParams { get; init; }
}

/// <summary>
/// Optional runtime parameters that influence SDP negotiation output.
/// </summary>
public sealed class SdpMediaNegotiationOptions
{
    /// <summary>
    /// Video negotiation parameters. <see langword="null"/> (default) keeps calls
    /// audio-only: offered video m-lines are declined with a zero-port answer line
    /// (RFC 3264 §6) and locally built offers carry no video.
    /// </summary>
    public SdpVideoNegotiationOptions? Video { get; init; }
    /// <summary>
    /// ICE settings to include in local SDP.
    /// </summary>
    public SdpIceNegotiationOptions? Ice { get; init; }

    /// <summary>
    /// Local DTLS identity. Required for answering a DTLS-SRTP offer (the answer carries
    /// our fingerprint and the resolved <c>a=setup</c> role, RFC 5763 §5) and for offering
    /// DTLS-SRTP when <see cref="OfferDtlsSrtp"/> is set. <see langword="null"/> disables
    /// DTLS signaling entirely.
    /// </summary>
    public SdpDtlsNegotiationOptions? Dtls { get; init; }

    /// <summary>
    /// When <see langword="true"/>, a locally built offer advertises DTLS-SRTP keying
    /// (RFC 5763): <c>UDP/TLS/RTP/SAVPF</c> profile, <c>a=fingerprint</c>, and
    /// <c>a=setup:actpass</c> — and suppresses SDES <c>a=crypto</c> lines (the keying
    /// methods are mutually exclusive per offer). Ignored on the answer path, which keys
    /// according to what the peer offered. Requires <see cref="Dtls"/>.
    /// </summary>
    public bool OfferDtlsSrtp { get; init; }

    /// <summary>
    /// When <see langword="true"/>, a locally built offer advertises SDES SRTP (RFC 4568):
    /// one <c>a=crypto</c> line with freshly generated key material plus the <c>RTP/SAVP</c>
    /// profile. Ignored on the answer path, which keys via the offered crypto.
    /// <see langword="false"/> keeps a plain <c>RTP/AVP</c> offer.
    /// </summary>
    public bool OfferSrtpCrypto { get; init; }

    /// <summary>
    /// Inline key params (<c>inline:…</c>) to reuse in the offered <c>a=crypto</c> line
    /// instead of generating fresh material. Set on a hold/unhold re-offer of an SRTP call
    /// so the offered key matches the running media context (no rekey). Only honoured when
    /// <see cref="OfferSrtpCrypto"/> is <see langword="true"/>; <see langword="null"/>
    /// generates a fresh key (initial offer).
    /// </summary>
    public string? OfferSrtpKeyParams { get; init; }

    /// <summary>
    /// Ordered audio codec preference by SDP encoding name (e.g. "PCMU", "PCMA", "G722").
    /// When set, local offers and answers only include the listed codecs (plus DTMF
    /// telephone-event) in this order, and the primary codec for RTP sessions is chosen
    /// by this preference. Names not supported by the SDK are ignored; when nothing
    /// matches, the SDK default codec set is used. <see langword="null"/> keeps defaults.
    /// </summary>
    public IReadOnlyList<string>? PreferredCodecNames { get; init; }

    /// <summary>
    /// Origin session id (<c>o=</c> sess-id, RFC 4566 §5.2) for locally built SDP. Stable
    /// across a call leg. <c>0</c> keeps the legacy constant.
    /// </summary>
    public long SessionId { get; init; }

    /// <summary>
    /// Origin session version (<c>o=</c> sess-version, RFC 4566 §5.2) for locally built SDP.
    /// The caller increments it on every media change (offer/answer/hold/unhold/re-INVITE) so
    /// the peer detects the modification (RFC 3264 §5). <c>0</c> keeps the legacy constant.
    /// </summary>
    public long SessionVersion { get; init; }
}
