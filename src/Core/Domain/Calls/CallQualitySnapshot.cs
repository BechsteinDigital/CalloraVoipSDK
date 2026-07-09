namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Public quality snapshot for one call media leg.
/// Values are updated continuously while the call is active and can be used
/// by SDK consumers for routing, alerting, or UI quality indicators.
/// </summary>
/// <param name="RemoteMosListeningQuality">
/// Listening-quality MOS (1.0–5.0) reported by the peer via RTCP-XR VoIP Metrics (RFC 3611 §4.7),
/// or <see langword="null"/> when the peer sent no XR or marked it unavailable.
/// </param>
/// <param name="RemoteMosConversationalQuality">
/// Conversational-quality MOS (1.0–5.0) reported by the peer via RTCP-XR VoIP Metrics, or
/// <see langword="null"/> when unavailable.
/// </param>
public readonly record struct CallQualitySnapshot(
    DateTimeOffset CapturedAtUtc,
    bool RtcpActive,
    bool RtcpMux,
    double LocalReceiveJitterMs,
    double LocalReceivePacketLossPercent,
    double? RemoteReportJitterMs,
    double? RemoteReportPacketLossPercent,
    double? RoundTripTimeMs,
    long RtcpPacketsSent,
    long RtcpPacketsReceived,
    double? RemoteMosListeningQuality = null,
    double? RemoteMosConversationalQuality = null)
{
    /// <summary>
    /// Creates an empty quality snapshot used before media is negotiated.
    /// </summary>
    public static CallQualitySnapshot CreateEmpty(DateTimeOffset capturedAtUtc, bool rtcpMux = false)
        => new(
            capturedAtUtc,
            RtcpActive: false,
            RtcpMux: rtcpMux,
            LocalReceiveJitterMs: 0,
            LocalReceivePacketLossPercent: 0,
            RemoteReportJitterMs: null,
            RemoteReportPacketLossPercent: null,
            RoundTripTimeMs: null,
            RtcpPacketsSent: 0,
            RtcpPacketsReceived: 0);
}
