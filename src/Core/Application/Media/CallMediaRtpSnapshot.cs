namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Internal RTP runtime snapshot used by the RTCP quality monitor.
/// Captures sender and receiver counters that are required to build SR/RR packets
/// and to compute call quality metrics.
/// </summary>
internal readonly record struct CallMediaRtpSnapshot(
    DateTimeOffset CapturedAtUtc,
    uint LocalSsrc,
    uint? RemoteSsrc,
    uint SenderPacketCount,
    uint SenderOctetCount,
    uint LastSentRtpTimestamp,
    bool HasSentRtpPackets,
    uint PacketsExpected,
    uint PacketsReceived,
    byte FractionLost,
    int CumulativePacketsLost,
    uint ExtendedHighestSequenceNumber,
    uint InterarrivalJitterRtpUnits,
    double LocalReceiveJitterMs,
    double LocalReceivePacketLossPercent,
    double LocalRoundTripTimeHintMs,
    // Monotonic time elapsed since the packet carrying LastSentRtpTimestamp went out, or null when nothing
    // has been sent. An elapsed span, not an instant: this layer keeps its own monotonic origin, so only a
    // delta measured on the sender's clock crosses the boundary intact. Last and optional so existing
    // positional construction — the test fakes in particular — stays valid (#162 P2-8).
    TimeSpan? SinceLastRtpSend = null);
