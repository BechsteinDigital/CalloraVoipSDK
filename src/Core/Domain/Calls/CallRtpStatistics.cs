namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Raw RTP transport statistics for one active call media leg, exposed for diagnostics,
/// billing and troubleshooting. Unlike <see cref="CallQualitySnapshot"/> — which surfaces
/// derived quality metrics (jitter/loss in ms/%, MOS) — this record carries the underlying
/// RFC 3550 counters (SSRC identifiers, packet/octet counts, RTP-unit jitter) without
/// interpretation. Available on <see cref="ICall.RtpStatistics"/> once media is flowing.
/// </summary>
/// <param name="CapturedAtUtc">UTC timestamp at which these counters were sampled.</param>
/// <param name="LocalSsrc">Our synchronization source identifier (RFC 3550 §5.1).</param>
/// <param name="RemoteSsrc">
/// The peer's synchronization source identifier once at least one RTP/RTCP packet has been
/// received from it; <see langword="null"/> before the remote SSRC is known.
/// </param>
/// <param name="PacketsSent">Total RTP packets we have sent on this leg (RTCP SR sender count).</param>
/// <param name="OctetsSent">Total RTP payload octets we have sent on this leg (RTCP SR octet count).</param>
/// <param name="PacketsReceived">Total RTP packets received from the peer on this leg.</param>
/// <param name="PacketsExpected">
/// Number of RTP packets expected from the peer based on the observed sequence-number range
/// (RFC 3550 §A.3); <c>PacketsExpected - PacketsReceived</c> approximates loss.
/// </param>
/// <param name="CumulativePacketsLost">
/// Cumulative number of RTP packets from the peer lost since the start of reception
/// (RFC 3550 §6.4.1); may be negative when duplicates arrive.
/// </param>
/// <param name="FractionLost">
/// The 8-bit fixed-point loss fraction over the last reporting interval (RFC 3550 §6.4.1):
/// the loss ratio is <c>FractionLost / 256.0</c>.
/// </param>
/// <param name="ExtendedHighestSequenceNumber">
/// Extended highest RTP sequence number received from the peer (RFC 3550 §6.4.1): the highest
/// 16-bit sequence number combined with the wrap-around count in the upper bits.
/// </param>
/// <param name="InterarrivalJitterRtpUnits">
/// Interarrival jitter estimate in RTP timestamp units (RFC 3550 §6.4.1). Divide by the codec
/// clock rate to convert to seconds.
/// </param>
public readonly record struct CallRtpStatistics(
    DateTimeOffset CapturedAtUtc,
    uint LocalSsrc,
    uint? RemoteSsrc,
    uint PacketsSent,
    uint OctetsSent,
    uint PacketsReceived,
    uint PacketsExpected,
    int CumulativePacketsLost,
    byte FractionLost,
    uint ExtendedHighestSequenceNumber,
    uint InterarrivalJitterRtpUnits);
