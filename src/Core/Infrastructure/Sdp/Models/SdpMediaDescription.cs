namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// One SDP media section (RFC 4566 §5.14, updated by RFC 8866).
/// Carries all media-level attributes required for RFC 3264 offer/answer,
/// RFC 4568 (SDES), RFC 5761 (rtcp-mux), RFC 5888 (BUNDLE), and RFC 8839 (ICE).
/// </summary>
internal sealed class SdpMediaDescription
{
    /// <summary>Media type token (<c>audio</c>, <c>video</c>, …).</summary>
    public required string MediaType { get; init; }

    /// <summary>
    /// RTP port for this media section, or <c>0</c> to reject / disable the stream
    /// (RFC 8866 §5.14 — zero-port semantics replace RFC 4566 zero-port).
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// <c>true</c> when the m-line port is 0, meaning this stream is disabled / rejected.
    /// </summary>
    public bool Disabled => Port == 0;

    /// <summary>RTP profile token (<c>RTP/AVP</c>, <c>RTP/SAVP</c>, <c>UDP/TLS/RTP/SAVPF</c>, …).</summary>
    public required string Profile { get; init; }

    /// <summary>Codec payload definitions, in declaration order.</summary>
    public required IReadOnlyList<SdpCodecDefinition> Codecs { get; init; }

    /// <summary>Media-level direction.</summary>
    public required SdpMediaDirection Direction { get; init; }

    // -------------------------------------------------------------------------
    // Timing / packetisation
    // -------------------------------------------------------------------------

    /// <summary>Preferred packetisation period in ms (<c>a=ptime</c>).</summary>
    public int? Ptime { get; init; }

    /// <summary>Maximum packetisation period in ms (<c>a=maxptime</c>).</summary>
    public int? MaxPtime { get; init; }

    // -------------------------------------------------------------------------
    // RTCP
    // -------------------------------------------------------------------------

    /// <summary>Whether RTP and RTCP are multiplexed on one port (<c>a=rtcp-mux</c>, RFC 5761).</summary>
    public bool RtcpMux { get; init; }

    /// <summary>Separate RTCP port, if distinct from RTP port (<c>a=rtcp:PORT</c>).</summary>
    public int? RtcpPort { get; init; }

    // -------------------------------------------------------------------------
    // BUNDLE / MID (RFC 5888)
    // -------------------------------------------------------------------------

    /// <summary>Media Identification tag for BUNDLE grouping (<c>a=mid</c>, RFC 5888).</summary>
    public string? Mid { get; init; }

    // -------------------------------------------------------------------------
    // Bandwidth
    // -------------------------------------------------------------------------

    /// <summary>Application-specific bandwidth limit in kbps (<c>b=AS:N</c>).</summary>
    public int? Bandwidth { get; init; }

    // -------------------------------------------------------------------------
    // Format parameters (RFC 4566 §6.6)
    // -------------------------------------------------------------------------

    /// <summary>Format-specific parameter lines (<c>a=fmtp</c>), keyed by payload type.</summary>
    public IReadOnlyList<SdpFmtpAttribute> Fmtp { get; init; } = [];

    /// <summary>RTCP feedback capabilities (<c>a=rtcp-fb</c>, RFC 4585 §4.2).</summary>
    public IReadOnlyList<SdpRtcpFeedback> RtcpFeedback { get; init; } = [];

    // -------------------------------------------------------------------------
    // ICE (RFC 8839)
    // -------------------------------------------------------------------------

    /// <summary>ICE candidates for this media section (<c>a=candidate</c>).</summary>
    public IReadOnlyList<SdpIceCandidate> Candidates { get; init; } = [];

    /// <summary>Media-level ICE username fragment (<c>a=ice-ufrag</c>).</summary>
    public string? IceUfrag { get; init; }

    /// <summary>Media-level ICE password (<c>a=ice-pwd</c>).</summary>
    public string? IcePwd { get; init; }

    /// <summary>Media-level ICE options string (<c>a=ice-options</c>).</summary>
    public string? IceOptions { get; init; }

    /// <summary>Whether the <c>a=end-of-candidates</c> attribute is present (RFC 8840).</summary>
    public bool EndOfCandidates { get; init; }

    // -------------------------------------------------------------------------
    // DTLS-SRTP (RFC 5763 / RFC 8122 / RFC 4145)
    // -------------------------------------------------------------------------

    /// <summary>
    /// DTLS certificate fingerprint (<c>a=fingerprint</c>, RFC 8122).
    /// <see langword="null"/> when not present.
    /// </summary>
    public SdpFingerprint? Fingerprint { get; init; }

    /// <summary>
    /// DTLS setup role (<c>a=setup</c>, RFC 4145).
    /// One of <c>actpass</c>, <c>active</c>, <c>passive</c>, or <c>holdconn</c>.
    /// <see langword="null"/> when not present.
    /// </summary>
    public string? DtlsSetup { get; init; }

    // -------------------------------------------------------------------------
    // SDES / SRTP (RFC 4568)
    // -------------------------------------------------------------------------

    /// <summary>SDES crypto offers for this media section (<c>a=crypto</c>).</summary>
    public IReadOnlyList<SdpCryptoAttribute> Crypto { get; init; } = [];

    // -------------------------------------------------------------------------
    // Per-media connection address (RFC 4566 §5.7)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Per-media connection address from <c>c=</c> line, overriding the session-level address.
    /// <see langword="null"/> when the session-level connection address should be used.
    /// </summary>
    public string? ConnectionAddress { get; init; }
}
