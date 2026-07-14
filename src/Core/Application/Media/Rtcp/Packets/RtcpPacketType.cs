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

    /// <summary>Transport-layer feedback (RTPFB, PT=205) — RFC 4585 §6.2 (Generic NACK).</summary>
    TransportFeedback = 205,

    /// <summary>Payload-specific feedback (PSFB, PT=206) — RFC 4585 §6.3 (PLI) / RFC 5104 §4.3 (FIR).</summary>
    PayloadFeedback   = 206,

    /// <summary>Extended Report (XR, PT=207) — RFC 3611.</summary>
    ExtendedReport = 207,
}
