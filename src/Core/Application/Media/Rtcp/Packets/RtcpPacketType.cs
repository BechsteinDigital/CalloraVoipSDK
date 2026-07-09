namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP packet type values (RFC 3550 §6.1).
/// </summary>
internal enum RtcpPacketType : byte
{
    SenderReport   = 200,
    ReceiverReport = 201,
    Sdes           = 202,
    Bye            = 203,
    App            = 204,
}
