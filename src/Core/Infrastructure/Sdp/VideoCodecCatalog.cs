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
    /// <remarks>
    /// #160 P2-10: this used to be a substring test, so <c>packetization-mode=10</c> — and any parameter
    /// whose *value* happened to end in that text, e.g. <c>profile-level-id=42packetization-mode=1</c> —
    /// read as mode 1. The consequence is not cosmetic: mode 0 means the peer cannot receive the FU-A
    /// fragments this stack emits, so a wrong "yes" here produces video the far end silently drops.
    /// The parameter is now matched as an exact key with an exact value (RFC 6184 §8.1).
    /// </remarks>
    public static bool HasPacketizationMode1(IReadOnlyList<SdpFmtpAttribute> fmtp, int payloadType) =>
        fmtp.Any(f => f.PayloadType == payloadType && DeclaresPacketizationMode1(f.Parameters));

    // fmtp parameters are a semicolon-separated list of key=value pairs (RFC 4566 §6). Split on the
    // separator and compare both halves whole, rather than searching the concatenated text.
    private static bool DeclaresPacketizationMode1(string parameters)
    {
        var remaining = parameters.AsSpan();
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOf(';');
            var pair = (separator < 0 ? remaining : remaining[..separator]).Trim();
            remaining = separator < 0 ? default : remaining[(separator + 1)..];

            var eq = pair.IndexOf('=');
            if (eq < 0)
                continue;

            if (pair[..eq].Trim().Equals("packetization-mode", StringComparison.OrdinalIgnoreCase)
                && pair[(eq + 1)..].Trim().SequenceEqual("1"))
            {
                return true;
            }
        }

        return false;
    }

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
    /// Answers RTCP feedback with the intersection of what the peer offered and what the SDK
    /// implements (RFC 4585 §4.2 — only mutually supported feedback is negotiated), echoing each
    /// offered entry with the payload type it was offered for.
    /// </summary>
    /// <remarks>
    /// #160 P2-7: the answer used to be built from <see cref="StandardFeedback"/>, which always says
    /// <c>*</c>. An offer of <c>a=rtcp-fb:96 ccm fir</c> was answered <c>a=rtcp-fb:* ccm fir</c> —
    /// claiming feedback for <em>every</em> format, including payload types the peer never offered it
    /// for. The earlier reasoning ("* is a superset, and there is only one video codec") stopped
    /// holding once RTX put a second payload type on the same m-line.
    ///
    /// It also contradicted this stack's own rule: <c>SdpAnswerValidator</c> rejects a remote answer
    /// that widens feedback beyond the offer (<c>UnofferedRtcpFeedback</c>). An answer must not do what
    /// it refuses to accept.
    ///
    /// Entries for a payload type that was not accepted are dropped — feedback for a format that is
    /// not in the answer describes nothing.
    /// </remarks>
    public static IReadOnlyList<SdpRtcpFeedback> NegotiateFeedback(
        IReadOnlyList<SdpRtcpFeedback> offered,
        IReadOnlySet<int> acceptedPayloadTypes)
    {
        var answered = new List<SdpRtcpFeedback>(offered.Count);
        var seen = new HashSet<(string Pt, string Type, string? Param)>();

        foreach (var theirs in offered)
        {
            var supported = StandardFeedback.Any(mine =>
                mine.FeedbackType.Equals(theirs.FeedbackType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mine.Parameter, theirs.Parameter, StringComparison.OrdinalIgnoreCase));
            if (!supported)
                continue;

            // "*" applies to every format; a numeric payload type only counts when we kept that format.
            var isWildcard = theirs.PayloadType == "*";
            if (!isWildcard
                && (!int.TryParse(theirs.PayloadType, out var pt) || !acceptedPayloadTypes.Contains(pt)))
            {
                continue;
            }

            // A duplicate line says nothing new and must not be echoed twice.
            if (!seen.Add((theirs.PayloadType, theirs.FeedbackType.ToLowerInvariant(), theirs.Parameter?.ToLowerInvariant())))
                continue;

            answered.Add(new SdpRtcpFeedback
            {
                PayloadType = theirs.PayloadType,
                FeedbackType = theirs.FeedbackType,
                Parameter = theirs.Parameter,
            });
        }

        return answered;
    }

    /// <summary>The SDP codec name of an RTX repair stream (RFC 4588 §8.1).</summary>
    public const string RtxCodecName = "rtx";

    /// <summary>The highest valid RTP payload type — the field is 7-bit (RFC 3550 §5.1).</summary>
    private const int MaxPayloadType = 127;

    /// <summary>
    /// Builds one RTX repair codec (RFC 4588 §8.1) per video codec for an offer:
    /// <c>a=rtpmap:&lt;pt&gt; rtx/90000</c> plus <c>a=fmtp:&lt;pt&gt; apt=&lt;origpt&gt;</c>.
    /// RTX payload types are assigned above the highest video payload type, capped at the
    /// 7-bit maximum (127, RFC 3550 §5.1): if no free payload type ≤127 remains for a codec,
    /// RTX is omitted for it rather than emitting an out-of-range payload type.
    /// </summary>
    public static (IReadOnlyList<SdpCodecDefinition> RtxCodecs, IReadOnlyList<SdpFmtpAttribute> AptFmtp)
        BuildRtx(IReadOnlyList<SdpCodecDefinition> videoCodecs)
    {
        // Payload types already claimed by the offered video codecs — never reuse them.
        var used = new HashSet<int>(videoCodecs.Select(c => c.PayloadType));
        var nextPt = videoCodecs.Count == 0 ? 96 : videoCodecs.Max(c => c.PayloadType) + 1;
        var rtx = new List<SdpCodecDefinition>(videoCodecs.Count);
        var fmtp = new List<SdpFmtpAttribute>(videoCodecs.Count);
        foreach (var codec in videoCodecs)
        {
            // Advance to the next free payload type ≤127; skip RTX for this codec when exhausted.
            while (nextPt <= MaxPayloadType && used.Contains(nextPt))
                nextPt++;
            if (nextPt > MaxPayloadType)
                continue;

            var pt = nextPt++;
            used.Add(pt);
            rtx.Add(new SdpCodecDefinition { PayloadType = pt, Name = RtxCodecName, ClockRate = codec.ClockRate });
            fmtp.Add(new SdpFmtpAttribute { PayloadType = pt, Parameters = $"apt={codec.PayloadType}" });
        }

        return (rtx, fmtp);
    }

    /// <summary>
    /// Answers RTX by echoing the RTX repair codecs the peer offered for codecs we accepted
    /// (RFC 4588 §8.1): keeps the offered rtx payload type and its <c>apt</c> so both sides
    /// agree on the numbering. Ignores RTX codecs whose <c>apt</c> points to a codec we did
    /// not accept.
    /// </summary>
    public static (IReadOnlyList<SdpCodecDefinition> RtxCodecs, IReadOnlyList<SdpFmtpAttribute> AptFmtp)
        NegotiateRtx(SdpMediaDescription offered, IReadOnlySet<int> acceptedPts)
    {
        var rtx = new List<SdpCodecDefinition>();
        var fmtp = new List<SdpFmtpAttribute>();
        foreach (var codec in offered.Codecs.Where(c => c.Name.Equals(RtxCodecName, StringComparison.OrdinalIgnoreCase)))
        {
            var apt = TryReadApt(offered.Fmtp, codec.PayloadType);
            if (apt is null || !acceptedPts.Contains(apt.Value))
                continue;

            rtx.Add(codec);
            fmtp.Add(new SdpFmtpAttribute { PayloadType = codec.PayloadType, Parameters = $"apt={apt.Value}" });
        }

        return (rtx, fmtp);
    }

    /// <summary>
    /// Finds the RTX repair payload type associated with an original video payload type in
    /// a media section (RFC 4588 §8.1 <c>apt</c>). <see langword="null"/> when no RTX was
    /// negotiated for it.
    /// </summary>
    public static int? TryFindRtxPayloadType(SdpMediaDescription media, int originalPayloadType)
    {
        foreach (var codec in media.Codecs.Where(c => c.Name.Equals(RtxCodecName, StringComparison.OrdinalIgnoreCase)))
        {
            if (TryReadApt(media.Fmtp, codec.PayloadType) == originalPayloadType)
                return codec.PayloadType;
        }

        return null;
    }

    private static int? TryReadApt(IReadOnlyList<SdpFmtpAttribute> fmtp, int rtxPayloadType)
    {
        var line = fmtp.FirstOrDefault(f => f.PayloadType == rtxPayloadType);
        if (line is null)
            return null;

        foreach (var token in line.Parameters.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("apt=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(token.AsSpan(4), out var apt))
            {
                return apt;
            }
        }

        return null;
    }
}
