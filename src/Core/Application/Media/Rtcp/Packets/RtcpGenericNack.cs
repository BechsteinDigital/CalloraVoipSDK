namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP Generic NACK (RTPFB PT=205, FMT=1) — RFC 4585 §6.2.1. Reports RTP packets a
/// receiver detected as lost, so the sender can retransmit them (via RTX, RFC 4588, once
/// wired). Each entry names a base packet id plus a bitmask of the 16 following packets,
/// so one entry reports up to 17 lost sequence numbers.
/// </summary>
internal sealed class RtcpGenericNack : RtcpPacket
{
    /// <summary>Feedback message type (FMT) for Generic NACK within RTPFB (RFC 4585 §6.2.1).</summary>
    public const int FeedbackMessageType = 1;

    public override RtcpPacketType Type => RtcpPacketType.TransportFeedback;

    /// <summary>SSRC of the endpoint sending the feedback (the receiver).</summary>
    public required uint SenderSsrc { get; init; }

    /// <summary>SSRC of the media source whose packets were lost (the sender).</summary>
    public required uint MediaSsrc { get; init; }

    /// <summary>The NACK entries (each a PID + bitmask of lost following packets).</summary>
    public required IReadOnlyList<RtcpNackEntry> Entries { get; init; }

    /// <summary>
    /// Expands all entries into the flat list of lost RTP sequence numbers (PID plus each
    /// set bit of the bitmask, RFC 4585 §6.2.1). Sequence numbers wrap at 16 bits.
    /// </summary>
    public IReadOnlyList<ushort> LostSequenceNumbers()
    {
        var lost = new List<ushort>();
        foreach (var entry in Entries)
        {
            lost.Add(entry.PacketId);
            for (var bit = 0; bit < 16; bit++)
            {
                if ((entry.LostPacketBitmask & (1 << bit)) != 0)
                    lost.Add(unchecked((ushort)(entry.PacketId + bit + 1)));
            }
        }

        return lost;
    }
}
