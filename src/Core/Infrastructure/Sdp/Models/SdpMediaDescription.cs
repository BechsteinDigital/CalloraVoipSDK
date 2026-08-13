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

    /// <summary>
    /// Number of consecutive ports this section occupies (the <c>/n</c> suffix of
    /// <c>m=&lt;media&gt; &lt;port&gt;[/&lt;number of ports&gt;]</c>, RFC 8866 §5.14). <c>1</c> when absent.
    /// </summary>
    /// <remarks>
    /// #160 P2-11: the suffix used to fail the whole parse, so a peer offering
    /// <c>m=video 40000/2 RTP/AVP 96</c> — legal SDP, used by hierarchically encoded streams — got
    /// its entire description rejected. The count is kept rather than acted on: this stack uses one
    /// port per section, and treating a multi-port offer as single-port is what every peer here does.
    /// </remarks>
    public int PortCount { get; init; } = 1;

    /// <summary>RTP profile token (<c>RTP/AVP</c>, <c>RTP/SAVP</c>, <c>UDP/TLS/RTP/SAVPF</c>, …).</summary>
    public required string Profile { get; init; }

    /// <summary>
    /// The raw <c>fmt</c> tokens of the m-line, in declaration order.
    /// </summary>
    /// <remarks>
    /// #160 P2-11: for an RTP profile these are payload-type numbers and <see cref="Codecs"/> is the
    /// useful view. For any other profile the field is opaque (RFC 8866 §5.14) — <c>m=application 5000
    /// UDP/DTLS/SCTP webrtc-datachannel</c> names a protocol, not a payload type. Parsing those as
    /// integers dropped them silently, leaving a section whose format nobody could read.
    /// </remarks>
    public IReadOnlyList<string> Formats { get; init; } = [];

    /// <summary>Codec payload definitions, in declaration order. Empty for a non-RTP profile.</summary>
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

    /// <summary>
    /// Whether the peer will accept <em>only</em> multiplexed RTCP (<c>a=rtcp-mux-only</c>, RFC 8858).
    /// </summary>
    /// <remarks>
    /// #160 P2-9: previously not parsed at all. It says the offerer opened no separate RTCP port, so an
    /// answer without <c>a=rtcp-mux</c> sends RTCP where nothing is listening. RFC 8858 §4 requires the
    /// attribute to accompany <c>a=rtcp-mux</c>; this stack also treats it as a mux request on its own,
    /// since an offer carrying only <c>rtcp-mux-only</c> is unambiguous about what it wants and
    /// answering it non-muxed is the one case that actually breaks.
    /// </remarks>
    public bool RtcpMuxOnly { get; init; }

    /// <summary>
    /// Whether reduced-size RTCP is negotiated for this section (<c>a=rtcp-rsize</c>, RFC 5506).
    /// </summary>
    /// <remarks>
    /// #162 P2-3: without this, a feedback packet may not travel alone — RFC 3550 §6.1 requires every
    /// RTCP datagram to be a compound starting with SR/RR and carrying a CNAME. The attribute is what
    /// permits the single-packet form this stack already emits for transport-cc and keyframe feedback.
    /// </remarks>
    public bool ReducedSizeRtcp { get; init; }

    /// <summary>Whether RTP and RTCP are multiplexed on one port (<c>a=rtcp-mux</c>, RFC 5761).</summary>
    public bool RtcpMux { get; init; }

    /// <summary>Separate RTCP port, if distinct from RTP port (<c>a=rtcp:PORT</c>).</summary>
    public int? RtcpPort { get; init; }

    // -------------------------------------------------------------------------
    // BUNDLE / MID (RFC 5888)
    // -------------------------------------------------------------------------

    /// <summary>Media Identification tag for BUNDLE grouping (<c>a=mid</c>, RFC 5888).</summary>
    public string? Mid { get; init; }

    /// <summary>
    /// WebRTC MediaStream / track identity (<c>a=msid</c>, RFC 8830); <see langword="null"/> when absent.
    /// </summary>
    public SdpMsid? Msid { get; init; }

    // -------------------------------------------------------------------------
    // Bandwidth
    // -------------------------------------------------------------------------

    /// <summary>
    /// Bandwidth limit for this media section (<c>b=</c>, RFC 4566 §5.8), preserving the type
    /// token (<c>AS</c> kbit/s or <c>TIAS</c> bit/s) so it re-serializes unchanged.
    /// <see langword="null"/> when no <c>b=</c> line is present.
    /// </summary>
    /// <remarks>
    /// The first <c>b=</c> line of the section. A description may carry several with different type
    /// tokens — see <see cref="Bandwidths"/>.
    /// </remarks>
    public SdpBandwidth? Bandwidth => Bandwidths.Count > 0 ? Bandwidths[0] : null;

    /// <summary>
    /// Every <c>b=</c> line of this media section, in order (RFC 4566 §5.8).
    /// </summary>
    /// <remarks>
    /// #160 P3-18: a single field meant a second <c>b=</c> overwrote the first, so an offer carrying
    /// <c>b=AS:512</c> and <c>b=TIAS:500000</c> kept only the last — different type tokens describe
    /// different things and neither replaces the other. SIPSorcery keeps a list here too.
    /// </remarks>
    public IReadOnlyList<SdpBandwidth> Bandwidths { get; init; } = [];

    // -------------------------------------------------------------------------
    // Format parameters (RFC 4566 §6.6)
    // -------------------------------------------------------------------------

    /// <summary>Format-specific parameter lines (<c>a=fmtp</c>), keyed by payload type.</summary>
    public IReadOnlyList<SdpFmtpAttribute> Fmtp { get; init; } = [];

    /// <summary>RTCP feedback capabilities (<c>a=rtcp-fb</c>, RFC 4585 §4.2).</summary>
    public IReadOnlyList<SdpRtcpFeedback> RtcpFeedback { get; init; } = [];

    /// <summary>RTP header-extension mappings (<c>a=extmap</c>, RFC 8285 §5).</summary>
    public IReadOnlyList<SdpExtmap> Extensions { get; init; } = [];

    // -------------------------------------------------------------------------
    // Simulcast (RFC 8851 rid / RFC 8853 simulcast)
    // -------------------------------------------------------------------------

    /// <summary>RTP stream identifiers naming this section's encodings (<c>a=rid</c>, RFC 8851).</summary>
    public IReadOnlyList<SdpRid> Rids { get; init; } = [];

    /// <summary>Simulcast stream declaration (<c>a=simulcast</c>, RFC 8853); <see langword="null"/> when absent.</summary>
    public SdpSimulcast? Simulcast { get; init; }

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
