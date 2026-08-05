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
}
