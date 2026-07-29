using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Decodes each inbound decrypted RTCP compound on one BUNDLE media session and fans it out to the reception
/// statistics, the outbound quality tracker, the per-track RTCP feedback path, and the transport-wide congestion
/// plane (RFC 3550 §6.4.1, RFC 4585, RFC 8888). Extracted from <see cref="BundledMediaSession"/> so that session
/// stays a wiring/lifecycle unit under the 1000-line rule; the behaviour is byte-identical to the former inline
/// <c>OnControlPacketReceived</c>.
/// </summary>
/// <remarks>
/// Threading: driven solely by the single shared receive loop (via the inbound pipeline's control-packet event),
/// so it holds no synchronization of its own — every collaborator it touches is either receive-loop-confined or
/// internally synchronised. A malformed compound must not tear the loop down, so decode failures are swallowed
/// with a log (K4: parse failures are observable, never silent). The <see cref="BundledVideoTrackSet"/> and the
/// congestion plane are read live because a mid-call track add/remove mutates the video set — this dispatcher
/// reads whatever set the session hands it on each call, never a stale snapshot.
/// </remarks>
internal sealed class BundledInboundRtcpDispatcher
{
    private readonly IRtcpPacketCodec _rtcpCodec;
    private readonly BundledInboundReceptionStats _receptionStats;
    private readonly BundledOutboundQualityTracker _outboundQuality;
    private readonly BundledVideoTrackSet _video;
    private readonly BundledCongestionPlane? _congestion;
    private readonly ILogger _logger;

    /// <summary>Creates the dispatcher over the session's decode/record/fan-out collaborators.</summary>
    /// <param name="rtcpCodec">Decodes the inbound compound.</param>
    /// <param name="receptionStats">Records each Sender Report's LSR + arrival (RFC 3550 §6.4.1).</param>
    /// <param name="outboundQuality">Consumes the peer's report blocks about our outbound streams.</param>
    /// <param name="video">The video track set, fanned the decoded compound for PLI/FIR/NACK feedback.</param>
    /// <param name="congestion">The transport-wide congestion plane, or null when transport-cc was not negotiated.</param>
    /// <param name="logger">Logs an undecodable compound without propagating (the receive loop must survive it).</param>
    public BundledInboundRtcpDispatcher(
        IRtcpPacketCodec rtcpCodec,
        BundledInboundReceptionStats receptionStats,
        BundledOutboundQualityTracker outboundQuality,
        BundledVideoTrackSet video,
        BundledCongestionPlane? congestion,
        ILogger logger)
    {
        _rtcpCodec = rtcpCodec ?? throw new ArgumentNullException(nameof(rtcpCodec));
        _receptionStats = receptionStats ?? throw new ArgumentNullException(nameof(receptionStats));
        _outboundQuality = outboundQuality ?? throw new ArgumentNullException(nameof(outboundQuality));
        _video = video ?? throw new ArgumentNullException(nameof(video));
        _congestion = congestion;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Decodes an inbound decrypted RTCP compound (RFC 3550 §6.4.1) and fans it out. Two directions: every Sender
    /// Report's LSR (middle 32 NTP bits) + arrival is recorded per sender SSRC so our next report echoes LSR/DLSR
    /// back for the peer's RTT; and every report block the peer sends about OUR outbound streams (carried in an
    /// inbound SR or RR) feeds the outbound quality tracker to derive our own RTT and the loss the peer sees. The
    /// decoded compound is then fanned to each video track (PLI/FIR → key-frame request; Generic NACK → RTX) and to
    /// the transport-wide congestion controller (transport-cc feedback, RFC 8888). Runs on the receive loop; a
    /// malformed compound must not tear it down, so decode failures are swallowed with a log.
    /// </summary>
    /// <param name="rtcp">The decrypted RTCP compound bytes.</param>
    public void Dispatch(byte[] rtcp)
    {
        // Monotonic arrival for the RTT delta (matched against the SR's monotonic send instant) so a system-
        // clock step between sending our SR and its echo arriving cannot corrupt the derived RTT.
        var arrival = MonotonicClock.Now;

        IReadOnlyList<RtcpPacket> packets;
        try
        {
            packets = _rtcpCodec.Decode(rtcp);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Ignoring undecodable inbound RTCP compound on the bundle path.");
            return;
        }

        foreach (var packet in packets)
        {
            switch (packet)
            {
                case RtcpSenderReport senderReport:
                    _receptionStats.RecordSenderReport(senderReport.Ssrc, senderReport.NtpTimestamp);
                    RecordRemoteReportBlocks(senderReport.ReportBlocks, arrival);
                    break;
                case RtcpReceiverReport receiverReport:
                    RecordRemoteReportBlocks(receiverReport.ReportBlocks, arrival);
                    break;
            }
        }

        // Fan the already-decoded compound out to every video track for RTCP feedback (PLI/FIR → keyframe
        // request; Generic NACK → RTX). Each track filters to its own SSRC, so a NACK for one track never
        // resends another's. Runs on this same receive-loop thread, so each track's confinement is preserved.
        _video.OnRtcpPackets(packets);

        // And to the transport-wide congestion controller: any transport-cc feedback report in the compound
        // (RFC 8888) updates its delay-trend + loss estimators and the recommended bitrate. Same thread — no
        // added confinement concern.
        _congestion?.OnRtcpPackets(packets);
    }

    // Feeds the peer's reception report blocks (about our outbound streams) into the outbound quality tracker.
    private void RecordRemoteReportBlocks(IReadOnlyList<RtcpReportBlock> blocks, DateTimeOffset arrival)
    {
        foreach (var block in blocks)
            _outboundQuality.RecordRemoteReportBlock(
                block.Ssrc, block.FractionLost, block.LastSr, block.DelaySinceLastSr, arrival);
    }
}
