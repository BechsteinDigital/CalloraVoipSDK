namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP Picture Loss Indication (PSFB PT=206, FMT=1) — RFC 4585 §6.3.1. Sent by a video
/// receiver that lost enough of the stream to need a fresh reference frame; the encoder
/// responds with a keyframe. Carries no feedback control information — the two SSRCs
/// alone identify the affected stream.
/// </summary>
internal sealed class RtcpPictureLossIndication : RtcpPacket
{
    /// <summary>Feedback message type (FMT) for PLI within PSFB (RFC 4585 §6.3.1).</summary>
    public const int FeedbackMessageType = 1;

    public override RtcpPacketType Type => RtcpPacketType.PayloadFeedback;

    /// <summary>SSRC of the endpoint sending the feedback (the video receiver).</summary>
    public required uint SenderSsrc { get; init; }

    /// <summary>SSRC of the media source the picture loss applies to (the video sender).</summary>
    public required uint MediaSsrc { get; init; }
}
