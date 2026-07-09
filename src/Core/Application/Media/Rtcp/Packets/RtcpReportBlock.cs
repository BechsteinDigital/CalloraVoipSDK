namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// A single reception report block within an SR or RR packet (RFC 3550 §6.4.1).
/// Each block describes the reception quality from one source (24 bytes on the wire).
/// </summary>
internal sealed class RtcpReportBlock
{
    /// <summary>SSRC of the source this report describes.</summary>
    public required uint Ssrc { get; init; }

    /// <summary>
    /// Fraction of RTP packets lost since the last SR/RR, as a fixed-point value
    /// where 1.0 = 255 (8-bit unsigned). Zero if no packets were lost.
    /// </summary>
    public byte FractionLost { get; init; }

    /// <summary>
    /// Total number of RTP packets lost since reception began (24-bit signed,
    /// range −2^23 to 2^23−1; negative values indicate duplicate packets).
    /// </summary>
    public int CumulativePacketsLost { get; init; }

    /// <summary>
    /// Extended highest sequence number received: upper 16 bits are the
    /// rollover count, lower 16 bits are the highest sequence number seen.
    /// </summary>
    public uint ExtendedHighestSeq { get; init; }

    /// <summary>Interarrival jitter in RTP timestamp units (RFC 3550 §6.4.1).</summary>
    public uint Jitter { get; init; }

    /// <summary>
    /// Middle 32 bits of the NTP timestamp of the last SR received from this source.
    /// Zero if no SR has been received.
    /// </summary>
    public uint LastSr { get; init; }

    /// <summary>
    /// Delay since the last SR was received, in units of 1/65536 seconds.
    /// Zero if no SR has been received.
    /// </summary>
    public uint DelaySinceLastSr { get; init; }
}
