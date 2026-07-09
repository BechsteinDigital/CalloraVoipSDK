namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP Sender Report (SR, PT=200) — RFC 3550 §6.4.1.
/// Sent by active RTP senders; carries absolute wall-clock time and cumulative
/// send statistics together with up to 31 per-source reception report blocks.
/// </summary>
internal sealed class RtcpSenderReport : RtcpPacket
{
    public override RtcpPacketType Type => RtcpPacketType.SenderReport;

    /// <summary>SSRC of the sender emitting this report.</summary>
    public required uint Ssrc { get; init; }

    /// <summary>
    /// 64-bit NTP timestamp: upper 32 bits = seconds since 1 January 1900,
    /// lower 32 bits = fractional seconds (1/2^32 resolution).
    /// </summary>
    public ulong NtpTimestamp { get; init; }

    /// <summary>RTP timestamp corresponding to the NTP timestamp above.</summary>
    public uint RtpTimestamp { get; init; }

    /// <summary>Total RTP packets transmitted by the sender since beginning transmission.</summary>
    public uint SenderPacketCount { get; init; }

    /// <summary>Total RTP payload octets transmitted by the sender (not counting headers).</summary>
    public uint SenderOctetCount { get; init; }

    /// <summary>Up to 31 reception report blocks (one per active source).</summary>
    public IReadOnlyList<RtcpReportBlock> ReportBlocks { get; init; } = [];
}
