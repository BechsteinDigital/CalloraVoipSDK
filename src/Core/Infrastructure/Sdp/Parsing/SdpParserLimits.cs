namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

/// <summary>
/// Hard wire limits applied by <see cref="SdpSessionParser"/> before it splits and allocates from an
/// untrusted SDP body. A remote peer controls the size and shape of the SDP it sends; without these
/// caps the parser would allocate lines, media sections and per-section collections proportional to
/// attacker input (K4 Wire-DoS-Cap, ENGINEERING_RULES.md). Over-limit input is rejected as a controlled
/// parse failure, never an out-of-memory or an unbounded allocation.
/// <para>
/// The defaults are far above any real offer/answer (a large multi-track WebRTC offer with trickle
/// candidates is a few tens of KiB and a few hundred lines) so legitimate signalling is unaffected.
/// </para>
/// </summary>
internal sealed class SdpParserLimits
{
    /// <summary>Shared default limits.</summary>
    internal static SdpParserLimits Default { get; } = new();

    /// <summary>
    /// Maximum length of the whole SDP body (characters ≈ bytes for the ASCII/UTF-8 SDP grammar).
    /// Matches the SIP transport body cap so the otherwise-uncapped WebRTC path is bounded too.
    /// </summary>
    public int MaxSdpBytes { get; init; } = 256 * 1024;

    /// <summary>Maximum number of lines the body may contain.</summary>
    public int MaxLines { get; init; } = 8192;

    /// <summary>Maximum length of a single line.</summary>
    public int MaxLineBytes { get; init; } = 8192;

    /// <summary>Maximum number of <c>m=</c> media sections.</summary>
    public int MaxMediaSections { get; init; } = 128;

    // Per-media-section collection caps (#160 P1-1, part 2). Each attribute below appends to a per-section
    // list/map; without a typed cap a single m= section could hold as many entries as the whole body's line
    // budget allows. The defaults span the full valid RTP payload-type range and far exceed any real offer, so
    // legitimate signalling is unaffected; an over-limit section is a controlled parse failure (K4).

    /// <summary>Maximum payload types on one <c>m=</c> line (and rtpmap entries) — the full 0–127 range.</summary>
    public int MaxPayloadTypesPerMedia { get; init; } = 128;

    /// <summary>Maximum <c>a=fmtp</c> attributes per media section.</summary>
    public int MaxFmtpPerMedia { get; init; } = 128;

    /// <summary>Maximum <c>a=rtcp-fb</c> attributes per media section.</summary>
    public int MaxRtcpFeedbackPerMedia { get; init; } = 256;

    /// <summary>Maximum <c>a=extmap</c> header-extension mappings per media section.</summary>
    public int MaxHeaderExtensionsPerMedia { get; init; } = 64;

    /// <summary>Maximum <c>a=rid</c> attributes per media section.</summary>
    public int MaxRidsPerMedia { get; init; } = 64;

    /// <summary>Maximum embedded <c>a=candidate</c> attributes per media section.</summary>
    public int MaxIceCandidatesPerMedia { get; init; } = 256;

    /// <summary>Maximum <c>a=crypto</c> (SDES) attributes per media section.</summary>
    public int MaxCryptoPerMedia { get; init; } = 64;
}
