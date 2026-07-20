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
/// The RTP timestamp of the last packet the track sent. It is the anchor the reporter extrapolates from onto the
/// report instant (see <paramref name="LastRtpTimestampAtUtc"/> / <paramref name="ClockRate"/>, CF-004e); when
/// no extrapolation is possible (unknown send instant or clock rate) it is used as the SR's RTP timestamp as-is.
/// </param>
/// <param name="LastRtpTimestampAtUtc">
/// The wall-clock instant the last RTP timestamp was assigned (CF-004e). With <paramref name="ClockRate"/> the
/// reporter extrapolates the SR's RTP timestamp to the report instant:
/// <c>LastRtpTimestamp + round((reportInstant − LastRtpTimestampAtUtc) × ClockRate)</c>, so the SR's NTP and RTP
/// timestamps describe the same instant even across a send pause/DTX. Default (unset) disables extrapolation.
/// </param>
/// <param name="ClockRate">
/// The track's RTP clock rate (Hz) used for the extrapolation above (audio e.g. 48000, video 90000). Zero
/// disables extrapolation — the raw <paramref name="LastRtpTimestamp"/> is then used unchanged.
/// </param>
internal readonly record struct BundledSenderReportInfo(
    uint Ssrc,
    long PacketCount,
    long OctetCount,
    uint LastRtpTimestamp,
    DateTimeOffset LastRtpTimestampAtUtc = default,
    uint ClockRate = 0);
