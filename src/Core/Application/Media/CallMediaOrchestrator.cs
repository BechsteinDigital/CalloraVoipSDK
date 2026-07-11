using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Application service: coordinates RTP media session lifecycle with call state.
///
/// Responsibilities:
/// - Subscribes to <see cref="ICallChannel.MediaParametersNegotiated"/> per call.
/// - Creates a media session via <see cref="ICallMediaSessionFactory"/> when
///   SDP parameters are available (initial INVITE or re-INVITE).
/// - Wires inbound RTP frames to the call channel and outbound audio to RTP.
/// - Tears down the media session when the call terminates.
/// </summary>
internal sealed class CallMediaOrchestrator : IDisposable
{
    private readonly ICallMediaSessionFactory _sessionFactory;
    private readonly ICallIceAgent? _iceAgent;
    private readonly IRtcpPacketCodec _rtcpPacketCodec;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CallMediaOrchestrator> _logger;
    private readonly ConcurrentDictionary<CallId, ActiveMediaEntry> _active = new();
    private readonly ConcurrentDictionary<CallId, MediaActivity> _activity = new();
    private readonly MediaSupervisionOptions _supervision;

    // Read on the background ICE-setup task as well as the SIP/dispose threads — volatile
    // so the background task observes disposal promptly.
    private volatile bool _disposed;

    internal CallMediaOrchestrator(
        ICallMediaSessionFactory sessionFactory,
        ILoggerFactory loggerFactory,
        IRtcpPacketCodec rtcpPacketCodec,
        ICallIceAgent? iceAgent = null,
        MediaSupervisionOptions? supervision = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _rtcpPacketCodec = rtcpPacketCodec ?? throw new ArgumentNullException(nameof(rtcpPacketCodec));
        _iceAgent = iceAgent;
        _supervision = supervision ?? MediaSupervisionOptions.Default;
        _logger = _loggerFactory
            .CreateLogger<CallMediaOrchestrator>();
    }

    /// <summary>
    /// Attaches the orchestrator to one call's channel so it can react to
    /// media negotiation and call termination. Call once per call, immediately
    /// after the call object is created.
    /// </summary>
    internal void AttachCall(ICall call, ICallChannel channel)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(channel);

        channel.MediaParametersNegotiated += (_, parameters) =>
            OnMediaParametersNegotiated(call, channel, parameters);
    }

    /// <summary>
    /// Called by <see cref="Application.Calls.CallManager"/> when any call state changes.
    /// Tears down the media session when the call terminates.
    /// </summary>
    internal void OnCallStateChanged(object? sender, CallStateChangedEventArgs e)
    {
        if (e.NewState == CallState.Terminated)
        {
            _activity.TryRemove(e.Call.CallId, out _);
            _ = TeardownMediaAsync(e.Call.CallId);
        }
    }

    /// <summary>
    /// Hangs up a connected call whose inbound RTP has gone silent past
    /// <see cref="MediaSupervisionOptions.InboundMediaTimeout"/> — a NAT-safe fallback when a
    /// far-end BYE never reaches our in-dialog Contact. Disabled when the timeout is
    /// non-positive; on-hold calls are exempt unless explicitly configured. Fires at most
    /// once per call.
    /// </summary>
    private void CheckInboundMediaActivity(CallId callId, CallMediaRuntimeMetrics metrics)
    {
        var timeout = _supervision.InboundMediaTimeout;
        if (timeout <= TimeSpan.Zero)
            return;

        if (!_activity.TryGetValue(callId, out var activity))
            return;

        if (metrics.PacketsReceived > activity.LastReceived)
        {
            activity.LastReceived = metrics.PacketsReceived;
            activity.LastActivityUtc = DateTimeOffset.UtcNow;
            return;
        }

        // A held call legitimately carries no inbound RTP; only supervise it when configured.
        var supervisedState = activity.Call.State is CallState.Connected
            || (_supervision.HangupHeldCallOnSilence && activity.Call.State is CallState.OnHold);

        // No new inbound RTP: only act once media was flowing and the call is still supervised.
        if (activity.LastReceived == 0
            || DateTimeOffset.UtcNow - activity.LastActivityUtc < timeout
            || !supervisedState)
            return;

        if (Interlocked.Exchange(ref activity.HungUp, 1) != 0)
            return;

        _logger.LogInformation(
            "Call {CallId}: no inbound RTP for {Timeout}s — hanging up (far-end likely gone, BYE not received).",
            callId, timeout.TotalSeconds);
        _ = activity.Call.HangupAsync();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    private void OnMediaParametersNegotiated(
        ICall call,
        ICallChannel channel,
        CallMediaParameters parameters)
    {
        if (_disposed) return;

        // ICE candidate selection is async and may run STUN connectivity checks; doing it
        // inline would block the SIP signaling thread that raised this event. Non-ICE calls
        // resolve instantly, so they stay fully synchronous (unchanged ordering); ICE calls
        // complete media setup on a background task once the pair is selected.
        if (_iceAgent is null || !parameters.IceEnabled)
        {
            SetUpMediaSession(call, channel, parameters);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var effective = await ResolveIceCandidatePairAsync(call, parameters).ConfigureAwait(false);
                if (_disposed) return;
                SetUpMediaSession(call, channel, effective);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Media session setup failed after ICE selection for call {CallId}.", call.CallId);
            }
        });
    }

    private void SetUpMediaSession(ICall call, ICallChannel channel, CallMediaParameters effectiveParameters)
    {
        if (_disposed) return;

        _logger.LogDebug(
            "Media negotiated for call {CallId}: local={Local} remote={Remote} PT={PT}",
            call.CallId, effectiveParameters.LocalEndPoint, effectiveParameters.RemoteEndPoint, effectiveParameters.PayloadType);

        var sdkCall = call as Domain.Calls.Call;

        // Expose negotiated parameters on the call so the audio device can read them.
        if (sdkCall is not null)
            sdkCall.SetMediaParameters(effectiveParameters);

        // Tear down any prior session on re-INVITE before wiring the new one.
        if (_active.TryRemove(call.CallId, out var old))
        {
            UnwireSession(old);
            _ = old.QualityMonitor.DisposeAsync();
            _ = old.Session.DisposeAsync();
        }

        var session = _sessionFactory.Create(effectiveParameters);
        var qualityMonitor = new CallRtcpQualityMonitor(session, effectiveParameters, _loggerFactory, _rtcpPacketCodec);
        Action<CallAudioFrame> inboundHandler = frame => channel.DeliverInboundAudioFrame(frame);
        Action<byte, int> inboundDtmfHandler = (toneCode, durationMs) =>
            channel.DeliverInboundDtmf(toneCode, durationMs);
        Action<CallMediaRuntimeMetrics> metricsHandler = metrics =>
            OnRuntimeMetricsUpdated(call.CallId, metrics);
        Action<CallQualitySnapshot> qualityHandler = snapshot =>
            OnQualitySnapshotUpdated(call, snapshot, qualityMonitor);

        // Wire RTP inbound → call channel listeners (e.g. MediaReceiver)
        session.FrameReceived += inboundHandler;
        session.DtmfReceived += inboundDtmfHandler;
        session.RuntimeMetricsUpdated += metricsHandler;
        qualityMonitor.QualitySnapshotUpdated += qualityHandler;

        // Wire call channel send → RTP outbound
        channel.SetAudioSendDelegate((frame, ct) => session.SendFrameAsync(frame, ct));
        channel.SetDtmfSendDelegate((toneCode, durationMs, ct) =>
            session.SendDtmfAsync(toneCode, durationMs, ct));

        if (sdkCall is not null)
            sdkCall.SetQualitySnapshot(qualityMonitor.GetLatestSnapshot());

        var entry = new ActiveMediaEntry(
            session,
            qualityMonitor,
            channel,
            inboundHandler,
            inboundDtmfHandler,
            metricsHandler,
            qualityHandler);
        _active[call.CallId] = entry;
        _activity[call.CallId] = new MediaActivity { Call = call, LastActivityUtc = DateTimeOffset.UtcNow };

        _ = StartSessionAsync(call.CallId, entry);
    }

    private async Task StartSessionAsync(CallId callId, ActiveMediaEntry entry)
    {
        try
        {
            await entry.Session.StartAsync().ConfigureAwait(false);
            await entry.QualityMonitor.StartAsync().ConfigureAwait(false);
            _logger.LogDebug("Media session started for call {CallId}.", callId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start media session for call {CallId}.", callId);
        }
    }

    private async Task TeardownMediaAsync(CallId callId)
    {
        _activity.TryRemove(callId, out _);
        if (!_active.TryRemove(callId, out var entry)) return;

        try
        {
            var snapshot = entry.Session.GetRuntimeMetricsSnapshot();
            _logger.LogInformation(
                "Media metrics for call {CallId}: recv={Recv} queued={Queued} delivered={Delivered} conceal={Conceal} dropLate={Late} dropOverflow={Overflow} dropDuplicate={Duplicate} dropUnrecoverable={Unrecoverable} jitterMs={JitterMs:F2} delayMs={DelayMs:F2} rttMs={RttMs:F2} buffered={Buffered}.",
                callId,
                snapshot.PacketsReceived,
                snapshot.PacketsQueued,
                snapshot.PacketsDelivered,
                snapshot.PacketsConcealed,
                snapshot.PacketsDroppedLate,
                snapshot.PacketsDroppedOverflow,
                snapshot.PacketsDroppedDuplicate,
                snapshot.PacketsUnrecoverableLoss,
                snapshot.EstimatedJitterMs,
                snapshot.AdaptiveDelayMs,
                snapshot.EstimatedRoundTripTimeMs,
                snapshot.BufferedPackets);
            UnwireSession(entry);
            await entry.QualityMonitor.DisposeAsync().ConfigureAwait(false);
            await entry.Session.DisposeAsync().ConfigureAwait(false);
            _logger.LogDebug("Media session torn down for call {CallId}.", callId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error tearing down media session for call {CallId}.", callId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var entry in _active.Values)
        {
            UnwireSession(entry);
            _ = entry.QualityMonitor.DisposeAsync();
            _ = entry.Session.DisposeAsync();
        }

        _active.Clear();
    }

    // ──────────────────────────────────────────────────────────────────────────

    private void OnRuntimeMetricsUpdated(CallId callId, CallMediaRuntimeMetrics metrics)
    {
        CheckInboundMediaActivity(callId, metrics);

        _logger.LogDebug(
            "Media metrics update for call {CallId}: recv={Recv} queued={Queued} delivered={Delivered} conceal={Conceal} late={Late} overflow={Overflow} duplicate={Duplicate} unrecoverable={Unrecoverable} jitterMs={JitterMs:F2} delayMs={DelayMs:F2} rttMs={RttMs:F2} buffered={Buffered}.",
            callId,
            metrics.PacketsReceived,
            metrics.PacketsQueued,
            metrics.PacketsDelivered,
            metrics.PacketsConcealed,
            metrics.PacketsDroppedLate,
            metrics.PacketsDroppedOverflow,
            metrics.PacketsDroppedDuplicate,
            metrics.PacketsUnrecoverableLoss,
            metrics.EstimatedJitterMs,
            metrics.AdaptiveDelayMs,
            metrics.EstimatedRoundTripTimeMs,
            metrics.BufferedPackets);
    }

    private void OnQualitySnapshotUpdated(
        ICall call,
        CallQualitySnapshot snapshot,
        CallRtcpQualityMonitor monitor)
    {
        if (call is not Domain.Calls.Call sdkCall)
            return;

        sdkCall.SetQualitySnapshot(snapshot);

        var rtpSnapshot = monitor.GetLatestRtpSnapshot();
        if (rtpSnapshot is not null)
            sdkCall.SetRtpStatistics(CallRtpStatisticsFactory.From(rtpSnapshot.Value));

        _logger.LogDebug(
            "Call quality update for call {CallId}: active={Active} mux={Mux} localJitterMs={LocalJitterMs:F2} localLossPct={LocalLossPct:F2} remoteJitterMs={RemoteJitterMs:F2} remoteLossPct={RemoteLossPct:F2} rttMs={RttMs:F2} rtcpSent={RtcpSent} rtcpRecv={RtcpRecv}.",
            call.CallId,
            snapshot.RtcpActive,
            snapshot.RtcpMux,
            snapshot.LocalReceiveJitterMs,
            snapshot.LocalReceivePacketLossPercent,
            snapshot.RemoteReportJitterMs ?? 0,
            snapshot.RemoteReportPacketLossPercent ?? 0,
            snapshot.RoundTripTimeMs ?? 0,
            snapshot.RtcpPacketsSent,
            snapshot.RtcpPacketsReceived);
    }

    private static void UnwireSession(ActiveMediaEntry entry)
    {
        entry.Session.FrameReceived -= entry.InboundHandler;
        entry.Session.DtmfReceived -= entry.InboundDtmfHandler;
        entry.Session.RuntimeMetricsUpdated -= entry.MetricsHandler;
        entry.QualityMonitor.QualitySnapshotUpdated -= entry.QualityHandler;
        entry.Channel.SetAudioSendDelegate(null);
        entry.Channel.SetDtmfSendDelegate(null);
    }

    private async Task<CallMediaParameters> ResolveIceCandidatePairAsync(ICall call, CallMediaParameters parameters)
    {
        if (_iceAgent is null || !parameters.IceEnabled)
            return parameters;

        var callId = call.CallId;
        try
        {
            var selection = await _iceAgent
                .SelectCandidatePairAsync(callId, parameters, CancellationToken.None)
                .ConfigureAwait(false);

            // Surface the ICE outcome (state + selected pair) read-only on the call.
            (call as Domain.Calls.Call)?.SetIceSnapshot(CallIceSnapshotFactory.From(selection));

            _logger.LogInformation(
                "ICE selection for call {CallId}: state={State} selected={Selected} reason={ReasonCode}.",
                callId,
                selection.State,
                selection.HasSelectedPair,
                selection.ReasonCode);

            if (!selection.HasSelectedPair
                || selection.LocalEndPoint is null
                || selection.RemoteEndPoint is null)
            {
                return parameters;
            }

            var localRtcp = parameters.RtcpMux
                ? selection.LocalEndPoint
                : parameters.LocalRtcpEndPoint;
            var remoteRtcp = parameters.RtcpMux
                ? selection.RemoteEndPoint
                : parameters.RemoteRtcpEndPoint;

            return new CallMediaParameters
            {
                LocalEndPoint = selection.LocalEndPoint,
                RemoteEndPoint = selection.RemoteEndPoint,
                RtcpMux = parameters.RtcpMux,
                LocalRtcpEndPoint = localRtcp,
                RemoteRtcpEndPoint = remoteRtcp,
                PayloadType = parameters.PayloadType,
                CodecName = parameters.CodecName,
                PayloadTypeCodecMap = parameters.PayloadTypeCodecMap,
                TelephoneEventPayloadType = parameters.TelephoneEventPayloadType,
                ClockRate = parameters.ClockRate,
                SamplesPerPacket = parameters.SamplesPerPacket,
                MediaProfile = parameters.MediaProfile,
                IsSrtpNegotiated = parameters.IsSrtpNegotiated,
                AppliedSrtpPolicy = parameters.AppliedSrtpPolicy,
                SrtpDecisionReasonCode = parameters.SrtpDecisionReasonCode,
                IceEnabled = parameters.IceEnabled,
                LocalIceUfrag = parameters.LocalIceUfrag,
                LocalIcePwd = parameters.LocalIcePwd,
                LocalIceOptions = parameters.LocalIceOptions,
                LocalIceCandidates = parameters.LocalIceCandidates,
                RemoteIceUfrag = parameters.RemoteIceUfrag,
                RemoteIcePwd = parameters.RemoteIcePwd,
                RemoteIceOptions = parameters.RemoteIceOptions,
                RemoteIceCandidates = parameters.RemoteIceCandidates,
                RemoteIceEndOfCandidates = parameters.RemoteIceEndOfCandidates
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ICE selection failed for call {CallId}; using negotiated SDP endpoints.", callId);
            (call as Domain.Calls.Call)?.SetIceSnapshot(new CallIceSnapshot(
                CallIceState.Failed,
                HasSelectedPair: false,
                Nominated: false,
                LocalCandidate: null,
                RemoteCandidate: null,
                SelectedLocalEndPoint: null,
                SelectedRemoteEndPoint: null));
            return parameters;
        }
    }

}
