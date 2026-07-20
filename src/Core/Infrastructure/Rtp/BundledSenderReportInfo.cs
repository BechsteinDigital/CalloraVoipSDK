namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// A point-in-time snapshot of one outbound track's RTCP Sender Report counters (RFC 3550 §6.4.1),
/// captured by <see cref="BundledOutboundPipeline.SnapshotSenderReports"/> for the periodic reporter to
/// build a Sender Report from. Only tracks that have actually sent a packet are snapshotted.
/// </summary>
/// <param name="Ssrc">The track's synchronisation source.</param>
/// <param name="PacketCount">Total RTP packets the track has sent (the SR sender's packet count).</param>
/// <param name="OctetCount">Total RTP payload octets sent, excluding headers (the SR sender's octet count).</param>
/// <param name="LastRtpTimestamp">
/// The RTP timestamp of the last packet the track sent. The reporter uses it as the SR's RTP timestamp — an
/// approximation of the exact wall-clock↔RTP correspondence (which would extrapolate the current instant onto
/// the track's clock); a later slice can refine it. Good enough for a receiver to align RTP and NTP time.
/// </param>
internal readonly record struct BundledSenderReportInfo(
    uint Ssrc,
    long PacketCount,
    long OctetCount,
    uint LastRtpTimestamp);
