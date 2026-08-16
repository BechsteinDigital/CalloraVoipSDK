using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;

/// <summary>
/// Adaptive jitter buffer — accepts out-of-order RTP packets and delivers them
/// in sequence at the appropriate playout time (pull model).
/// </summary>
internal interface IJitterBuffer
{
    /// <summary>Number of packets currently held in the buffer.</summary>
    int BufferedCount { get; }

    /// <summary>Current adaptive playout delay in milliseconds.</summary>
    double CurrentDelayMs { get; }

    /// <summary>Estimated inter-arrival jitter in milliseconds (RFC 3550 §6.4.1).</summary>
    double EstimatedJitterMs { get; }

    /// <summary>
    /// Smoothed RTT estimate in milliseconds used by adaptive delay control.
    /// </summary>
    double EstimatedRoundTripTimeMs { get; }

    /// <summary>
    /// Adds an RTP packet to the buffer.
    /// </summary>
    /// <param name="packet">The received RTP packet.</param>
    /// <param name="arrivalTime">Wall-clock time at which the packet arrived.</param>
    /// <returns>Whether the packet was accepted, rejected as late/duplicate, or dropped due to overflow.</returns>
    JitterBufferAddResult Add(RtpPacket packet, DateTimeOffset arrivalTime);

    /// <summary>
    /// Updates the RTT hint used by the adaptive delay controller.
    /// The buffer applies smoothing internally to avoid oscillations.
    /// </summary>
    /// <param name="roundTripTimeMs">Observed RTT in milliseconds.</param>
    void UpdateRoundTripTime(double roundTripTimeMs);

    /// <summary>
    /// Drops every buffered packet and forgets the stream state — sequence numbering, the playout
    /// reference point, and the jitter estimate — so the next packet starts a fresh stream. Used when the
    /// remote synchronisation source changes (#161 P2-6): the new source brings its own sequence and
    /// timestamp space, which has no relation to the previous one. The adaptive delay and the RTT estimate
    /// are kept: they describe the path, not the source.
    /// </summary>
    void Reset();

    /// <summary>
    /// Retrieves the next packet whose scheduled playout time has arrived, or
    /// <c>null</c> if no packet is ready yet.
    /// Packets are delivered strictly in sequence-number order.
    /// </summary>
    /// <param name="now">Current wall-clock time.</param>
    RtpPacket? TryGetNext(DateTimeOffset now);
}
