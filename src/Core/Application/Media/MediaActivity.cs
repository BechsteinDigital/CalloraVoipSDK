using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Per-call media supervision state and the decision it drives (#261, ADR-069). Media progress (inbound RTP)
/// and peer liveness (inbound RTP <em>or</em> RTCP) are tracked separately, because they answer different
/// questions: "is there audio" versus "is the far end still there". Conflating them is what made a peer using
/// silence suppression (RFC 3389), a peer on hold, or a peer mid-bridge-switch during a transfer look dead.
/// </summary>
/// <remarks>
/// Threading: <see cref="Observe"/> runs on the media/RTCP metrics callback, which is single-consumer per
/// call, so the mutable counters need no lock. <see cref="_hungUp"/> is guarded with
/// <see cref="Interlocked"/> anyway so the teardown verdict can never be issued twice.
/// <see cref="CallMediaOrchestrator"/> owns the effects; this type only decides.
/// </remarks>
internal sealed class MediaActivity
{
    /// <summary>The call this tracker belongs to.</summary>
    public required ICall Call { get; init; }

    /// <summary>When supervision started; the baseline for both clocks until the first packet arrives.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    private long _lastRtpReceived;
    private long _lastRtcpReceived;
    private DateTimeOffset? _lastMediaUtc;
    private DateTimeOffset? _lastLivenessUtc;
    private bool _mediaFlowing = true;
    private int _hungUp;

    /// <summary>Total inbound RTP packets last observed — exposed for diagnostics and tests.</summary>
    public long LastRtpReceived => _lastRtpReceived;

    /// <summary>Whether inbound media is currently considered to be flowing.</summary>
    public bool MediaFlowing => _mediaFlowing;

    /// <summary>
    /// Folds one metrics observation into the supervision state and returns what it means.
    /// </summary>
    /// <param name="metrics">The media session's latest runtime metrics.</param>
    /// <param name="state">The call's current state; a held call is exempt from the teardown unless configured.</param>
    /// <param name="options">The configured thresholds.</param>
    /// <param name="now">The observation instant (injected so the policy is testable without waiting).</param>
    public MediaSupervisionOutcome Observe(
        CallMediaRuntimeMetrics metrics,
        CallState state,
        MediaSupervisionOptions options,
        DateTimeOffset now)
    {
        var mediaProgressed = metrics.PacketsReceived > _lastRtpReceived;
        var rtcpProgressed = metrics.RtcpPacketsReceived > _lastRtcpReceived;

        // Captured before the update: on a resume the reported duration is the silence that just ended, not
        // the zero-length span since the packet we are looking at.
        var silenceStartedAt = _lastMediaUtc ?? StartedUtc;

        if (mediaProgressed)
        {
            _lastRtpReceived = metrics.PacketsReceived;
            _lastMediaUtc = now;
        }

        if (rtcpProgressed)
            _lastRtcpReceived = metrics.RtcpPacketsReceived;

        // Liveness is RTP OR RTCP: either one proves the far end is still there and reachable.
        if (mediaProgressed || rtcpProgressed)
            _lastLivenessUtc = now;

        // Supervision starts once media has actually flowed. A call that never received a packet at all is a
        // negotiation problem, not a supervision one, and stays with the signalling layer.
        if (_lastRtpReceived == 0)
            return MediaSupervisionOutcome.None;

        var silence = now - silenceStartedAt;

        if (mediaProgressed)
        {
            if (_mediaFlowing)
                return MediaSupervisionOutcome.None;

            _mediaFlowing = true;
            return new MediaSupervisionOutcome(MediaSupervisionVerdict.MediaResumed, silence);
        }

        // Teardown first: a peer that has stopped sending everything is gone, and reporting silence for it
        // would be a notification the application cannot act on any more. The flow flag goes down with it so
        // no silence notification trails the teardown on the next tick.
        if (IsPeerGone(state, options, now))
        {
            _mediaFlowing = false;
            return new MediaSupervisionOutcome(MediaSupervisionVerdict.PeerGone, silence);
        }

        var notifyAfter = options.MediaSilenceNotifyAfter;
        if (!_mediaFlowing || notifyAfter <= TimeSpan.Zero || silence < notifyAfter)
            return MediaSupervisionOutcome.None;

        _mediaFlowing = false;
        return new MediaSupervisionOutcome(MediaSupervisionVerdict.MediaSilent, silence);
    }

    // True once the peer has sent neither RTP nor RTCP for the configured timeout, at most once per call.
    private bool IsPeerGone(CallState state, MediaSupervisionOptions options, DateTimeOffset now)
    {
        var timeout = options.InboundMediaTimeout;
        if (timeout <= TimeSpan.Zero)
            return false;

        // A held call legitimately carries no inbound media; only supervise it when configured.
        var supervised = state is CallState.Connected
            || (options.HangupHeldCallOnSilence && state is CallState.OnHold);

        if (!supervised || now - (_lastLivenessUtc ?? StartedUtc) < timeout)
            return false;

        return Interlocked.Exchange(ref _hungUp, 1) == 0;
    }
}
