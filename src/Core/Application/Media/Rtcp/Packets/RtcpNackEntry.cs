namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// One Generic NACK entry (RFC 4585 §6.2.1): a base RTP packet id (PID) and a 16-bit
/// bitmask (BLP) whose bit <c>i</c> flags packet <c>PID + i + 1</c> as also lost.
/// </summary>
internal sealed record RtcpNackEntry
{
    /// <summary>Base lost RTP sequence number.</summary>
    public required ushort PacketId { get; init; }

    /// <summary>Bitmask of the 16 packets following <see cref="PacketId"/> (bit 0 = PID+1).</summary>
    public required ushort LostPacketBitmask { get; init; }
}
