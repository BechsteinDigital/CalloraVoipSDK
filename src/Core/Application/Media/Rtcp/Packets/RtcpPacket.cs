namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// Base class for all RTCP packet types (RFC 3550 §6).
/// </summary>
internal abstract class RtcpPacket
{
    public abstract RtcpPacketType Type { get; }
}
