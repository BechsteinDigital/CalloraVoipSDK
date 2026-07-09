namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP Source Description packet (SDES, PT=202) — RFC 3550 §6.5.
/// Contains one or more SDES chunks, each describing one SSRC/CSRC.
/// Every compound RTCP packet must include an SDES with a CNAME item.
/// </summary>
internal sealed class RtcpSdesPacket : RtcpPacket
{
    public override RtcpPacketType Type => RtcpPacketType.Sdes;

    public IReadOnlyList<RtcpSdesChunk> Chunks { get; init; } = [];
}
