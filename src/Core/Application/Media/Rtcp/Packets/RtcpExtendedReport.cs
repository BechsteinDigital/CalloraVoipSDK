namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP Extended Report (XR, PT=207) — RFC 3611 §2. A container of typed report blocks; this
/// decoder surfaces the VoIP Metrics blocks (block type 7) that carry call-quality data. Other
/// block types are skipped over via their block length and not represented here.
/// </summary>
internal sealed class RtcpExtendedReport : RtcpPacket
{
    /// <inheritdoc />
    public override RtcpPacketType Type => RtcpPacketType.ExtendedReport;

    /// <summary>SSRC of the participant emitting this Extended Report.</summary>
    public required uint Ssrc { get; init; }

    /// <summary>VoIP Metrics blocks (RFC 3611 §4.7) carried by this report, in wire order.</summary>
    public IReadOnlyList<RtcpVoipMetricsBlock> VoipMetrics { get; init; } = [];
}
