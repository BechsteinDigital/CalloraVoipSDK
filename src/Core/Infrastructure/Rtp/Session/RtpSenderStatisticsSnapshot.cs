namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// Immutable RTP sender snapshot used by application orchestration and RTCP reporting.
/// </summary>
/// <param name="SinceLastSend">
/// Monotonic time elapsed since the packet carrying <paramref name="LastSentRtpTimestamp"/> went out, or
/// <see langword="null"/> when nothing has been sent yet. An elapsed span rather than an instant on purpose:
/// the Application layer keeps its own monotonic origin (it must not reference Infrastructure), so an instant
/// from this clock would be meaningless there — only a delta measured on one side crosses the boundary
/// intact. Consumed to extrapolate the SR's RTP timestamp onto the report instant (#162 P2-8).
/// </param>
internal readonly record struct RtpSenderStatisticsSnapshot(
    uint LocalSsrc,
    uint SenderPacketCount,
    uint SenderOctetCount,
    uint LastSentRtpTimestamp,
    bool HasSentPackets,
    TimeSpan? SinceLastSend = null);
