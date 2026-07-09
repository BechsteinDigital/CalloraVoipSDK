namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP Receiver Report (RR, PT=201) — RFC 3550 §6.4.2.
/// Sent by participants that are not active senders; carries up to 31
/// per-source reception report blocks.
/// </summary>
internal sealed class RtcpReceiverReport : RtcpPacket
{
    public override RtcpPacketType Type => RtcpPacketType.ReceiverReport;

    /// <summary>SSRC of the participant emitting this report.</summary>
    public required uint Ssrc { get; init; }

    /// <summary>Up to 31 reception report blocks (one per active source).</summary>
    public IReadOnlyList<RtcpReportBlock> ReportBlocks { get; init; } = [];
}
