namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// One SDP <c>a=rtcp-fb</c> attribute (RFC 4585 §4.2): the RTCP feedback a media section
/// supports, e.g. <c>a=rtcp-fb:* nack</c> (Generic NACK), <c>a=rtcp-fb:* nack pli</c>
/// (Picture Loss Indication), <c>a=rtcp-fb:* ccm fir</c> (Full Intra Request, RFC 5104).
/// The payload type is either a numeric PT or <c>*</c> (applies to all formats).
/// </summary>
internal sealed record SdpRtcpFeedback
{
    /// <summary>Payload type the feedback applies to — a number or <c>*</c> for all formats.</summary>
    public required string PayloadType { get; init; }

    /// <summary>Feedback type token, e.g. <c>nack</c>, <c>ccm</c>, <c>goog-remb</c>.</summary>
    public required string FeedbackType { get; init; }

    /// <summary>Optional feedback parameter, e.g. <c>pli</c> for nack or <c>fir</c> for ccm.</summary>
    public string? Parameter { get; init; }

    /// <summary>
    /// Parses the value after <c>a=rtcp-fb:</c> (<c>&lt;pt&gt; &lt;type&gt; [&lt;param&gt;]</c>).
    /// Returns <see langword="null"/> on malformed input.
    /// </summary>
    public static SdpRtcpFeedback? TryParse(string attrValue)
    {
        if (string.IsNullOrWhiteSpace(attrValue))
            return null;

        var parts = attrValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        return new SdpRtcpFeedback
        {
            PayloadType = parts[0],
            FeedbackType = parts[1],
            Parameter = parts.Length > 2 ? string.Join(' ', parts[2..]) : null,
        };
    }

    /// <summary>Serializes to the value string (without the leading <c>a=rtcp-fb:</c>).</summary>
    public string Serialize() =>
        Parameter is null
            ? $"{PayloadType} {FeedbackType}"
            : $"{PayloadType} {FeedbackType} {Parameter}";
}
