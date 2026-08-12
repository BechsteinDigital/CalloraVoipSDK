using System.Diagnostics;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The transport-wide congestion-control plane for one BUNDLE (transport-cc), factored out of
/// <see cref="BundledMediaSession"/>. transport-cc numbers the transport, not a stream, so there is exactly
/// one plane per bundle, and it is built only when the a=extmap was negotiated. Two halves:
/// <list type="bullet">
/// <item><description>Sender side — <see cref="TransportCcCongestionController"/>: records each stamped
/// outbound send (the pipeline's <c>PacketSent</c>) and folds inbound feedback reports into a delay-trend +
/// loss estimate → a recommended bitrate.</description></item>
/// <item><description>Receive side — <see cref="TransportCcFeedbackSender"/>: reads every inbound packet's
/// transport-wide sequence (across all MIDs) and, timer-driven, reports the arrivals back to the peer over
/// the shared fail-closed SRTCP send.</description></item>
/// </list>
/// </summary>
internal sealed class BundledCongestionPlane : IAsyncDisposable
{
    // Transport-cc congestion-control tuning (delay-trend EWMA + fixed overuse threshold + loss EWMA, then an
    // AIMD recommended-bitrate policy). Mirrors the single-stream VideoRtpStream defaults so both media paths
    // behave alike; an adaptive threshold / SCReAM (RFC 8298) is the later accuracy upgrade.
    private const int TransportCcSendHistoryCapacity = 4096;
    private const double TransportCcDelaySmoothing = 0.1;
    private const long TransportCcOveruseThresholdMicros = 5_000; // 5 ms
    private const double TransportCcLossSmoothing = 0.1;
    private const long TransportCcInitialVideoBitrateBps = 1_000_000; // 1 Mbps start
    private const long TransportCcMinVideoBitrateBps = 100_000;       // 100 kbps floor
    private const long TransportCcMaxVideoBitrateBps = 5_000_000;     // 5 Mbps ceiling
    private const long TransportCcBitrateIncreaseStepBps = 100_000;   // additive probe per healthy report
    private const double TransportCcBitrateDecreaseFactor = 0.85;     // multiplicative back-off on congestion
    private const double TransportCcBitrateLossThreshold = 0.1;       // ≥10% loss backs off regardless of delay

    private readonly TransportCcCongestionController _congestion;
    private readonly TransportCcFeedbackSender _feedback;
    // Cancels the feedback loop's in-flight sends at teardown (the sender's own dispose stops the loop; this
    // token aborts a send already awaiting the transport). Signalled first in DisposeAsync.
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>
    /// Builds the plane and wires both halves into the bundle pipelines: the sender-side controller to the
    /// outbound pipeline's <c>PacketSent</c>, and the receive-side feedback sender to the inbound pipeline's
    /// decoded RTP. The subscriptions live for the session (both pipelines are torn down with it).
    /// </summary>
    /// <param name="transportCcExtensionId">The negotiated transport-wide-cc header-extension id.</param>
    /// <param name="outbound">The bundle's outbound pipeline (PacketSent source and the SRTCP-protected send).</param>
    /// <param name="inbound">The bundle's inbound pipeline (decoded RTP across all MIDs).</param>
    /// <param name="rtcpCodec">The shared RTCP wire codec used to build feedback compounds.</param>
    /// <param name="feedbackSenderSsrc">The sender SSRC stamped on outbound transport-cc feedback.</param>
    /// <param name="loggerFactory">Builds the controller and feedback-sender loggers.</param>
    public BundledCongestionPlane(
        byte transportCcExtensionId,
        BundledOutboundPipeline outbound,
        BundledInboundPipeline inbound,
        IRtcpPacketCodec rtcpCodec,
        uint feedbackSenderSsrc,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(rtcpCodec);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _congestion = new TransportCcCongestionController(
            transportCcExtensionId,
            new TransportCcSendHistory(TransportCcSendHistoryCapacity),
            new TransportCcDelayTrendEstimator(TransportCcDelaySmoothing, TransportCcOveruseThresholdMicros),
            new TransportCcLossEstimator(TransportCcLossSmoothing),
            new CongestionBitrateController(
                TransportCcInitialVideoBitrateBps, TransportCcMinVideoBitrateBps, TransportCcMaxVideoBitrateBps,
                TransportCcBitrateIncreaseStepBps, TransportCcBitrateDecreaseFactor, TransportCcBitrateLossThreshold),
            Stopwatch.GetTimestamp, Stopwatch.Frequency,
            loggerFactory.CreateLogger<TransportCcCongestionController>());
        outbound.PacketSent += _congestion.OnPacketSent;

        _feedback = new TransportCcFeedbackSender(
            // Feedback keeps no reporting state, so the send outcome (#162 P2-5) is not consulted here: a
            // suppressed batch is simply the next batch's problem.
            rtcpCodec, transportCcExtensionId, feedbackSenderSsrc,
            async (datagram, ct) => await outbound.SendRtcpAsync(datagram, ct).ConfigureAwait(false),
            Stopwatch.GetTimestamp, Stopwatch.Frequency,
            loggerFactory.CreateLogger<TransportCcFeedbackSender>(), _lifetimeCts.Token);
        inbound.RtpPacketReceived += _feedback.OnRtpPacketReceived;
    }

    /// <summary>
    /// The bundle's sender-side transport-wide congestion controller — the recommended outbound bitrate and
    /// coarse network quality. Surfacing it on the public WebRTC facade is a documented follow-up.
    /// </summary>
    internal TransportCcCongestionController Controller => _congestion;

    /// <summary>
    /// Folds an already-decoded inbound RTCP compound's transport-cc feedback (transport-cc) into the sender-side
    /// delay-trend + loss estimators and the recommended bitrate. Runs on the receive loop.
    /// </summary>
    public void OnRtcpPackets(IReadOnlyList<RtcpPacket> packets) => _congestion.OnRtcpPackets(packets);

    /// <summary>
    /// Starts the receive-side feedback loop (transport-cc). Harmless before keying — its SRTCP send fails closed,
    /// so early ticks are an empty batch or a suppressed send.
    /// </summary>
    public void Start() => _feedback.Start();

    /// <summary>
    /// Stops the feedback loop before the shared transport it rides is torn down: signals the lifetime token so
    /// an in-flight send aborts, then awaits the loop. Must run before the transport is disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        await _feedback.DisposeAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();
    }
}
