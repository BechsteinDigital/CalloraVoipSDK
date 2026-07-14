namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP Full Intra Request (PSFB PT=206, FMT=4) — RFC 5104 §4.3.1. A reliable keyframe
/// request: unlike PLI it carries a per-source sequence number so the request is not
/// mistaken for a duplicate and can be retransmitted until answered. One or more entries
/// each target a media source.
/// </summary>
internal sealed class RtcpFullIntraRequest : RtcpPacket
{
    /// <summary>Feedback message type (FMT) for FIR within PSFB (RFC 5104 §4.3.1).</summary>
    public const int FeedbackMessageType = 4;

    public override RtcpPacketType Type => RtcpPacketType.PayloadFeedback;

    /// <summary>SSRC of the endpoint sending the request.</summary>
    public required uint SenderSsrc { get; init; }

    /// <summary>Per-source FIR entries (media SSRC + monotonic command sequence number).</summary>
    public required IReadOnlyList<RtcpFirEntry> Entries { get; init; }
}
