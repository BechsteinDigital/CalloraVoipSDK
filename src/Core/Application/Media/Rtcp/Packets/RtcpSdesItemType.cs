namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP SDES item types (RFC 3550 §6.5).
/// </summary>
internal enum RtcpSdesItemType : byte
{
    End   = 0,
    CName = 1,
    Name  = 2,
    Email = 3,
    Phone = 4,
    Loc   = 5,
    Tool  = 6,
    Note  = 7,
    Priv  = 8,
}
