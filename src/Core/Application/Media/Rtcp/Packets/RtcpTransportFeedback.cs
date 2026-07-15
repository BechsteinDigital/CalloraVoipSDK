namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP transport-wide congestion-control feedback (RTPFB PT=205, FMT=15 —
/// draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1). A receiver reports, keyed by the
/// transport-wide sequence number stamped in the RTP header extension (RFC 8285), which packets
/// arrived and their inter-arrival times, so the sender's congestion controller can estimate
/// available bandwidth. The pairing feedback for the transport-wide-cc extension this SDK stamps
/// on outgoing video.
/// </summary>
internal sealed class RtcpTransportFeedback : RtcpPacket
{
    /// <summary>Feedback message type (FMT) for transport-wide-cc within RTPFB (draft §3.1).</summary>
    public const int FeedbackMessageType = 15;

    public override RtcpPacketType Type => RtcpPacketType.TransportFeedback;

    /// <summary>SSRC of the endpoint sending the feedback (the receiver).</summary>
    public required uint SenderSsrc { get; init; }

    /// <summary>SSRC of the media source the feedback is about (the sender).</summary>
    public required uint MediaSsrc { get; init; }

    /// <summary>
    /// Reference time base in 64 ms ticks (draft §3.1, a signed 24-bit value). The absolute
    /// arrival time of the first received packet is this base plus its delta; each later delta is
    /// relative to the previous received packet.
    /// </summary>
    public required int ReferenceTimeTicks { get; init; }

    /// <summary>
    /// Monotonic counter incremented for each feedback message sent for this source (draft §3.1),
    /// wrapping at 8 bits — lets the sender detect lost or reordered feedback.
    /// </summary>
    public required byte FeedbackPacketCount { get; init; }

    /// <summary>
    /// Per-packet arrival statuses in ascending sequence order, starting at
    /// <see cref="RtcpTransportFeedbackStatus.SequenceNumber"/> of the first entry (the base
    /// sequence number). Must be non-empty and contiguous in sequence number.
    /// </summary>
    public required IReadOnlyList<RtcpTransportFeedbackStatus> Statuses { get; init; }
}
