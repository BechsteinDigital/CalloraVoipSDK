using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp;

/// <summary>
/// Video codec capabilities the SDK can negotiate (WebRTC phase 2): VP8 (RFC 7741)
/// and H.264 (RFC 6184), both at the mandatory 90 kHz RTP clock. Single source of
/// truth for SDP negotiation and media-parameter extraction.
/// </summary>
internal static class VideoCodecCatalog
{
    /// <summary>Default preference order: VP8 first (WebRTC baseline), then H.264.</summary>
    private static readonly IReadOnlyList<SdpCodecDefinition> Defaults =
    [
        new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 },
        new SdpCodecDefinition { PayloadType = 97, Name = "H264", ClockRate = 90000 },
    ];

    /// <summary>
    /// Resolves an ordered name preference to the supported codec definitions.
    /// Unknown names are ignored; <see langword="null"/> or no match yields the defaults.
    /// </summary>
    public static IReadOnlyList<SdpCodecDefinition> Resolve(IReadOnlyList<string>? preferredNames)
    {
        if (preferredNames is null || preferredNames.Count == 0)
            return Defaults;

        var resolved = preferredNames
            .Select(name => Defaults.FirstOrDefault(
                c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c is not null)
            .Cast<SdpCodecDefinition>()
            .ToArray();

        return resolved.Length > 0 ? resolved : Defaults;
    }

    /// <summary>
    /// True when the codec name is a video codec the SDK's packetisation layer supports.
    /// </summary>
    public static bool IsSupported(string codecName) =>
        Defaults.Any(c => c.Name.Equals(codecName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the fmtp of the given payload type explicitly declares
    /// <c>packetization-mode=1</c> (RFC 6184 §8.1). Absence means mode 0 — a peer that
    /// cannot receive the FU-A fragments the packetisation layer emits.
    /// </summary>
    public static bool HasPacketizationMode1(IReadOnlyList<SdpFmtpAttribute> fmtp, int payloadType) =>
        fmtp.Any(f => f.PayloadType == payloadType
                      && f.Parameters.Replace(" ", string.Empty, StringComparison.Ordinal)
                          .Contains("packetization-mode=1", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Offer-side fmtp lines for the given video codecs: H.264 announces
    /// <c>packetization-mode=1</c> (RFC 6184 §8.1 — matches the FU-A/STAP-A capabilities
    /// of the packetisation layer); VP8 needs no parameters.
    /// </summary>
    public static IReadOnlyList<SdpFmtpAttribute> BuildFmtp(IReadOnlyList<SdpCodecDefinition> codecs) =>
        codecs
            .Where(c => c.Name.Equals("H264", StringComparison.OrdinalIgnoreCase))
            .Select(c => new SdpFmtpAttribute { PayloadType = c.PayloadType, Parameters = "packetization-mode=1" })
            .ToArray();

    /// <summary>
    /// RTCP feedback the video media layer implements, offered/answered on every video
    /// m-line for all formats (<c>*</c>): Generic NACK (RFC 4585), Picture Loss Indication
    /// (RFC 4585 §6.3.1), and Full Intra Request (RFC 5104 §4.3.1). NACK is advertised for
    /// symmetry — the SDK currently sends PLI on loss; retransmission is follow-up work.
    /// </summary>
    public static IReadOnlyList<SdpRtcpFeedback> StandardFeedback { get; } =
    [
        new SdpRtcpFeedback { PayloadType = "*", FeedbackType = "nack" },
        new SdpRtcpFeedback { PayloadType = "*", FeedbackType = "nack", Parameter = "pli" },
        new SdpRtcpFeedback { PayloadType = "*", FeedbackType = "ccm", Parameter = "fir" },
    ];

    /// <summary>
    /// Answers RTCP feedback with the intersection of what the peer offered and what the
    /// SDK implements (RFC 4585 §4.2 — only mutually supported feedback is negotiated).
    /// Matched by feedback type and parameter, ignoring the payload-type field.
    /// DECISION: the answer always advertises for all formats (<c>*</c>) even when the peer
    /// offered a specific payload type (e.g. <c>96 ccm fir</c> is answered <c>* ccm fir</c>).
    /// <c>*</c> is a superset of any single PT, so this is interop-safe for the single-video-
    /// codec case; per-PT answer mirroring is deferred until multi-codec video needs it.
    /// </summary>
    public static IReadOnlyList<SdpRtcpFeedback> NegotiateFeedback(IReadOnlyList<SdpRtcpFeedback> offered) =>
        StandardFeedback
            .Where(mine => offered.Any(theirs =>
                theirs.FeedbackType.Equals(mine.FeedbackType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(theirs.Parameter, mine.Parameter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
}
