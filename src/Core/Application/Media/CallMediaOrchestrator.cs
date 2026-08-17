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
    // Monotonic per-call negotiation generation: bumped on every negotiation, so only the latest one may install
    // a session (a slower ICE selection cannot overwrite a newer one). Removed on teardown so a late ICE result
    // for a terminated call is rejected. Guards the register/displace/teardown mutations of _active (#10).
    private readonly ConcurrentDictionary<CallId, long> _mediaGeneration = new();
    // Per-call cancellation for the background ICE candidate-pair selection (#165 P1-2): terminating or
    // disposing cancels the STUN connectivity checks instead of letting them run to completion on an ambient
    // CancellationToken.None. Created on the first ICE-enabled negotiation, cancelled+removed on teardown.
    private readonly ConcurrentDictionary<CallId, CancellationTokenSource> _iceCancellation = new();
    private readonly object _setupSync = new();
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
            // Cancel any in-flight ICE selection so its STUN checks stop instead of running to completion for a
            // call that is already gone. Cancel-then-dispose is safe: the token was handed out before, and after
            // cancellation its consumers observe it without registering new callbacks.
            if (_iceCancellation.TryRemove(e.Call.CallId, out var iceCts))
            {
                iceCts.Cancel();
                iceCts.Dispose();
            }
            _ = TeardownMediaAsync(e.Call.CallId);
        }
    }

    /// <summary>
    /// Supervises a connected call's inbound media in two stages (#261, ADR-069).
    /// <para>
    /// Stage 1 — <b>media silence</b>: no inbound RTP for
    /// <see cref="MediaSupervisionOptions.MediaSilenceNotifyAfter"/> raises <c>ICall.MediaFlowChanged</c> and
    /// nothing else. Silence alone is not evidence of a dead peer: silence suppression (RFC 3389), hold, and
    /// the bridge switch of a transfer all produce it while the far end keeps reporting RTCP.
    /// </para>
    /// <para>
    /// Stage 2 — <b>loss of liveness</b>: no inbound RTP <em>and</em> no inbound RTCP for
    /// <see cref="MediaSupervisionOptions.InboundMediaTimeout"/> ends the call — the NAT-safe fallback for a
    /// far-end BYE that never reaches our in-dialog Contact. Fires at most once per call, carries a
    /// termination reason so a consumer can tell it from a peer BYE, and is disabled by a non-positive
    /// timeout. On-hold calls are exempt from stage 2 unless explicitly configured.
    /// </para>
    /// </summary>
    private void CheckInboundMediaActivity(CallId callId, CallMediaRuntimeMetrics metrics)
    {
        if (!_activity.TryGetValue(callId, out var activity))
            return;

        var outcome = activity.Observe(metrics, activity.Call.State, _supervision, DateTimeOffset.UtcNow);
        if (outcome.Verdict == MediaSupervisionVerdict.None)
            return;

        if (activity.Call is not Domain.Calls.Call sdkCall)
            return;

        switch (outcome.Verdict)
        {
            case MediaSupervisionVerdict.MediaSilent:
                _logger.LogInformation(
                    "Call {CallId}: no inbound media for {Silence}s while the peer is still reporting — "
                    + "surfacing media silence to the application.", callId, outcome.SilenceDuration.TotalSeconds);
                sdkCall.ReportMediaFlowChanged(inboundMediaFlowing: false, outcome.SilenceDuration);
                break;

            case MediaSupervisionVerdict.MediaResumed:
                _logger.LogInformation(
                    "Call {CallId}: inbound media resumed after {Silence}s of silence.",
                    callId, outcome.SilenceDuration.TotalSeconds);
                sdkCall.ReportMediaFlowChanged(inboundMediaFlowing: true, outcome.SilenceDuration);
                break;

            case MediaSupervisionVerdict.PeerGone:
                _logger.LogInformation(
                    "Call {CallId}: no inbound RTP or RTCP for {Timeout}s — hanging up (far-end gone, BYE not received).",
                    callId, _supervision.InboundMediaTimeout.TotalSeconds);
                _ = sdkCall.HangupAsync(MediaTimeoutReason);
                break;
        }
    }

    // The termination reason an SDK-initiated media-timeout teardown carries, so a consumer can tell it apart
    // from a peer BYE (FreeSWITCH surfaces the same distinction as its MEDIA_TIMEOUT hangup cause).
    private static readonly CallTerminationReason MediaTimeoutReason = new()
    {
        Category = CallTerminationCategory.Failed,
        TerminatedBy = CallTerminatedBy.Local,
        ReasonPhrase = "Media timeout: no inbound RTP or RTCP from the far end.",
    };

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    private void OnMediaParametersNegotiated(
        ICall call,
        ICallChannel channel,
        CallMediaParameters parameters)
    {
        if (_disposed) return;

        // Stamp this negotiation with a monotonic generation; only the latest generation may install a session,
        // so a slower ICE selection (e.g. from an earlier re-INVITE) cannot overwrite a newer one (#10).
        var generation = _mediaGeneration.AddOrUpdate(call.CallId, 1, static (_, current) => current + 1);

        // ICE candidate selection is async and may run STUN connectivity checks; doing it
        // inline would block the SIP signaling thread that raised this event. Non-ICE calls
        // resolve instantly, so they stay fully synchronous (unchanged ordering); ICE calls
        // complete media setup on a background task once the pair is selected.
        if (_iceAgent is null || !parameters.IceEnabled)
        {
            SetUpMediaSession(call, channel, parameters, generation);
            return;
        }

        // Tie the ICE selection to the call lifecycle so terminating the call cancels the STUN checks. Read the
        // token on this thread — a token value captured before teardown disposes the source stays usable for
        // observing cancellation, whereas reading .Token on the background task could race a dispose (ODE).
        var cts = _iceCancellation.GetOrAdd(call.CallId, static _ => new CancellationTokenSource());
        var iceCt = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var effective = await ResolveIceCandidatePairAsync(call, parameters, iceCt).ConfigureAwait(false);
                if (effective is null)
                {
                    // Fail-closed (#165 P1-2): ICE was offered but produced no validated candidate pair. Falling
                    // back to the SDP-advertised endpoints would send media to an address the peer never proved it
                    // controls (a connectivity-check bypass), so tear the call down instead of installing a session.
                    _logger.LogWarning(
                        "ICE produced no validated candidate pair for call {CallId}; failing the call closed "
                        + "instead of using the unvalidated SDP endpoints.", call.CallId);
                    _ = call.HangupAsync();
                    return;
                }
                SetUpMediaSession(call, channel, effective, generation);
            }
            catch (OperationCanceledException)
            {
                // The call terminated while ICE selection was still running — nothing to install or fail.
                _logger.LogDebug("ICE selection for call {CallId} was cancelled by call teardown.", call.CallId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Media session setup failed after ICE selection for call {CallId}.", call.CallId);
            }
        });
    }

    private void SetUpMediaSession(ICall call, ICallChannel channel, CallMediaParameters effectiveParameters, long generation)
    {
        // Cheap early-out when the orchestrator is gone. The authoritative guard against installing a session for
        // an already-terminated call (or a superseded negotiation) is re-evaluated under _setupSync just before
        // registration, so a session built here is disposed rather than installed if the call raced to teardown.
        if (_disposed) return;

        _logger.LogDebug(
            "Media negotiated for call {CallId}: local={Local} remote={Remote} PT={PT}",
            call.CallId, effectiveParameters.LocalEndPoint, effectiveParameters.RemoteEndPoint, effectiveParameters.PayloadType);

        var sdkCall = call as Domain.Calls.Call;

        // Expose negotiated parameters on the call so the audio device can read them.
        if (sdkCall is not null)
            sdkCall.SetMediaParameters(effectiveParameters);

        var session = _sessionFactory.Create(effectiveParameters);

        // Take the RTCP socket the channel reserved as a pair with the media socket (non-mux path) so the
        // monitor binds a port it already owns, instead of a late bind that can race concurrent call setup.
        System.Net.Sockets.UdpClient? rtcpSocket = null;
        if (channel is IRtcpSocketHandoff rtcpHandoff)
        {
            if (effectiveParameters.RtcpMux)
                // rtcp-mux: RTCP shares the RTP port, so the reserved N+1 socket is never used. Release it
                // now instead of leaving it bound in the channel until the call ends (one wasted UDP port
                // per call at scale).
                rtcpHandoff.TakeRtcpSocket()?.Dispose();
            else
                rtcpSocket = rtcpHandoff.TakeRtcpSocket();
        }

        var qualityMonitor = new CallRtcpQualityMonitor(
            session, effectiveParameters, _loggerFactory, _rtcpPacketCodec, preBoundRtcpSocket: rtcpSocket);
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

        // Surface a running ICE transport state from the consent monitor (RFC 7675): a transient degrade
        // → Disconnected (media keeps flowing, may recover), a later recovery → Connected, and consent
        // loss/expiry → Failed (media has ceased). The application can react (tear down or, later, trigger
        // an ICE restart). Only ICE legs raise these, so no gating is needed here.
        Action? consentLostHandler = null;
        Action? connectivityDegradedHandler = null;
        Action? connectivityRecoveredHandler = null;
        if (sdkCall is not null)
        {
            consentLostHandler = () => sdkCall.SetIceConnectionState(Domain.Calls.CallIceState.Failed);
            connectivityDegradedHandler = () => sdkCall.SetIceConnectionState(Domain.Calls.CallIceState.Disconnected);
            connectivityRecoveredHandler = () => sdkCall.SetIceConnectionState(Domain.Calls.CallIceState.Connected);
            session.MediaConsentLost += consentLostHandler;
            session.MediaConnectivityDegraded += connectivityDegradedHandler;
            session.MediaConnectivityRecovered += connectivityRecoveredHandler;
        }

        // Video sub-stream (WebRTC phase 2): present only when the session negotiated video —
        // wire it symmetrically to audio. The SDK is transport-only: the negotiated payload type
        // is fixed by the codec, and the frame's RTP timestamp drives packetisation. The
        // depacketiser classifies each reassembled frame, so IsKeyFrame reflects the real intra
        // flag (VP8 P-bit / H.264 IDR).
        var video = session.Video;
        Action<byte[], uint, bool>? inboundVideoHandler = null;
        Action? congestionHandler = null;
        Action? keyFrameHandler = null;
        if (video is not null)
        {
            var videoPayloadType = video.PayloadType;
            inboundVideoHandler = (encodedFrame, rtpTimestamp, isKeyFrame) =>
                channel.DeliverInboundVideoFrame(
                    new CallVideoFrame(encodedFrame, videoPayloadType, rtpTimestamp, isKeyFrame));
            video.FrameReceived += inboundVideoHandler;
            channel.SetVideoSendDelegate((frame, ct) => video.SendFrameAsync(frame.Payload, frame.RtpTimestamp, ct));

            // Push the SDK's ready-to-use bitrate recommendation + network quality onto the call so the
            // public video sender can read/subscribe. Prime it now, then refresh on each feedback report.
            if (sdkCall is not null)
            {
                sdkCall.SetVideoCongestion(video.RecommendedBitrateBps, video.NetworkQuality);
                congestionHandler = () => sdkCall.SetVideoCongestion(video.RecommendedBitrateBps, video.NetworkQuality);
                video.CongestionUpdated += congestionHandler;

                // Forward the peer's RTCP PLI/FIR keyframe request to the public video sender so the
                // application's encoder can emit an intra frame next.
                keyFrameHandler = () => sdkCall.RaiseVideoKeyFrameRequested();
                video.KeyFrameRequested += keyFrameHandler;
            }
        }

        if (sdkCall is not null)
            sdkCall.SetQualitySnapshot(qualityMonitor.GetLatestSnapshot());

        var entry = new ActiveMediaEntry(
            session,
            qualityMonitor,
            channel,
            inboundHandler,
            inboundDtmfHandler,
            metricsHandler,
            qualityHandler,
            video,
            inboundVideoHandler,
            congestionHandler,
            keyFrameHandler,
            consentLostHandler,
            connectivityDegradedHandler,
            connectivityRecoveredHandler);
        // Register atomically against teardown, re-checking the guard under the lock: a concurrent
        // termination/teardown or a newer negotiation must win the race — otherwise the session (RTP socket +
        // RTCP loops) would leak on an already-terminated call, with no Terminated event left to reap it (#10).
        ActiveMediaEntry? displaced = null;
        lock (_setupSync)
        {
            if (!CanInstallMediaSession(call, generation))
            {
                UnwireSession(entry);
                _ = qualityMonitor.DisposeAsync();
                _ = session.DisposeAsync();
                return;
            }

            _active.TryRemove(call.CallId, out displaced); // prior session on re-INVITE
            _active[call.CallId] = entry;
            _activity[call.CallId] = new MediaActivity { Call = call, StartedUtc = DateTimeOffset.UtcNow };
        }

        if (displaced is not null)
        {
            UnwireSession(displaced);
            _ = displaced.QualityMonitor.DisposeAsync();
            _ = displaced.Session.DisposeAsync();
        }

        _ = StartSessionAsync(call.CallId, entry);
    }

    // Whether a media session may still be installed for this negotiation: the orchestrator is live, the call has
    // not terminated, and this is still the latest negotiation generation for the call. Re-evaluated under
    // <see cref="_setupSync"/> at install time so a concurrent teardown/termination or a newer negotiation wins (#10).
    private bool CanInstallMediaSession(ICall call, long generation) =>
        !_disposed
        && call.State != CallState.Terminated
        && _mediaGeneration.TryGetValue(call.CallId, out var latest)
        && latest == generation;

    private async Task StartSessionAsync(CallId callId, ActiveMediaEntry entry)
    {
        try
        {
            await entry.Session.StartAsync().ConfigureAwait(false);
            await entry.QualityMonitor.StartAsync().ConfigureAwait(false);
            _logger.LogDebug("Media session started for call {CallId}.", callId);
            return;
        }
        catch (Exception ex)
        {
            // The entry is installed before the start runs — deliberately, so a packet arriving the moment the
            // socket opens finds it. But a failed start used to be a log line and nothing else (#165 P2-7): the
            // entry stayed in _active, holding an RTP socket and RTCP loops that were never started (or, when
            // the quality monitor was the half that failed, a session that was), with only the eventual call
            // teardown left to reap it. A partial start is rolled back to no start at all.
            _logger.LogWarning(ex, "Failed to start media session for call {CallId}; rolling it back.", callId);
        }

        await RollBackFailedStartAsync(callId, entry).ConfigureAwait(false);
    }

    // Removes a media entry whose start failed and releases it. Only this entry: a newer negotiation may have
    // displaced it in the meantime (and disposed it already), so the removal is a compare-and-remove under the
    // same lock the install takes, and the activity record goes only with a removal we actually made.
    private async Task RollBackFailedStartAsync(CallId callId, ActiveMediaEntry entry)
    {
        bool removed;
        lock (_setupSync)
        {
            removed = _active.TryRemove(new KeyValuePair<CallId, ActiveMediaEntry>(callId, entry));
            if (removed)
                _activity.TryRemove(callId, out _);
        }

        if (!removed)
            return; // superseded; whoever displaced it owns its disposal.

        UnwireSession(entry);
        try
        {
            await entry.QualityMonitor.DisposeAsync().ConfigureAwait(false);
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing the media session of call {CallId} after a failed start faulted.", callId);
        }
    }

    private async Task TeardownMediaAsync(CallId callId)
    {
        _activity.TryRemove(callId, out _);
        // Supersede any in-flight negotiation so a late ICE result cannot install a session after teardown, and
        // remove the active entry under the same lock the install takes so the two cannot race (#10).
        _mediaGeneration.TryRemove(callId, out _);
        ActiveMediaEntry? entry;
        lock (_setupSync)
            _active.TryRemove(callId, out entry);
        if (entry is null) return;

        try
        {
            var snapshot = entry.Session.GetRuntimeMetricsSnapshot();
            LogMediaMetrics(LogLevel.Information, callId, snapshot);
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

        // Drain the active sessions under the install lock, and with _disposed already set, so a late install
        // (its guard now sees _disposed) can neither slip an entry past this drain nor start after it (#10).
        ActiveMediaEntry[] entries;
        lock (_setupSync)
        {
            entries = _active.Values.ToArray();
            _active.Clear();
        }

        foreach (var entry in entries)
        {
            UnwireSession(entry);
            // Synchronous Dispose cannot await these long-running teardowns without risking a deadlock, but the
            // fire-and-forget ValueTasks must not swallow a teardown fault — observe and log it instead of
            // discarding it via `_ =` (#17.12), mirroring the async TeardownMediaAsync path.
            ObserveDisposeFault(entry.QualityMonitor.DisposeAsync(), "quality monitor");
            ObserveDisposeFault(entry.Session.DisposeAsync(), "media session");
        }

        // Cancel and dispose any ICE selections still tied to a live call so their STUN checks stop. The
        // _disposed guard in OnMediaParametersNegotiated already blocks new entries past this point.
        foreach (var iceCts in _iceCancellation.Values)
        {
            iceCts.Cancel();
            iceCts.Dispose();
        }
        _iceCancellation.Clear();
    }

    // Observes a fire-and-forget DisposeAsync started from the synchronous Dispose: the ValueTask cannot be
    // awaited there, but a teardown fault must not vanish unobserved — it is logged (#17.12). Never faults.
    private void ObserveDisposeFault(ValueTask disposal, string what)
    {
        if (disposal.IsCompletedSuccessfully)
            return;

        _ = AwaitDisposeAsync(disposal, what);
    }

    private async Task AwaitDisposeAsync(ValueTask disposal, string what)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing {What} during media orchestrator shutdown.", what);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────

    private void OnRuntimeMetricsUpdated(CallId callId, CallMediaRuntimeMetrics metrics)
    {
        CheckInboundMediaActivity(callId, metrics);

        LogMediaMetrics(LogLevel.Debug, callId, metrics);
    }

    /// <summary>
    /// Emits the per-call media-metrics line. The teardown summary (Information) and the periodic
    /// update (Debug) logged the same 13 fields and differed only in level and wording; they now
    /// share this one template (HARD-R6).
    /// </summary>
    private void LogMediaMetrics(LogLevel level, CallId callId, CallMediaRuntimeMetrics metrics)
    {
        _logger.Log(
            level,
            "Media metrics for call {CallId}: recv={Recv} queued={Queued} delivered={Delivered} conceal={Conceal} dropLate={Late} dropOverflow={Overflow} dropDuplicate={Duplicate} dropUnrecoverable={Unrecoverable} jitterMs={JitterMs:F2} delayMs={DelayMs:F2} rttMs={RttMs:F2} buffered={Buffered}.",
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
        if (entry.Video is not null && entry.InboundVideoHandler is not null)
            entry.Video.FrameReceived -= entry.InboundVideoHandler;
        if (entry.Video is not null && entry.CongestionHandler is not null)
            entry.Video.CongestionUpdated -= entry.CongestionHandler;
        if (entry.Video is not null && entry.KeyFrameHandler is not null)
            entry.Video.KeyFrameRequested -= entry.KeyFrameHandler;
        if (entry.ConsentLostHandler is not null)
            entry.Session.MediaConsentLost -= entry.ConsentLostHandler;
        if (entry.ConnectivityDegradedHandler is not null)
            entry.Session.MediaConnectivityDegraded -= entry.ConnectivityDegradedHandler;
        if (entry.ConnectivityRecoveredHandler is not null)
            entry.Session.MediaConnectivityRecovered -= entry.ConnectivityRecoveredHandler;
        entry.Channel.SetVideoSendDelegate(null);
    }

    private async Task<CallMediaParameters?> ResolveIceCandidatePairAsync(
        ICall call, CallMediaParameters parameters, CancellationToken ct)
    {
        if (_iceAgent is null || !parameters.IceEnabled)
            return parameters;

        var callId = call.CallId;
        try
        {
            var selection = await _iceAgent
                .SelectCandidatePairAsync(callId, parameters, ct)
                .ConfigureAwait(false);

            // Surface the ICE outcome (state + selected pair) read-only on the call.
            (call as Domain.Calls.Call)?.SetIceSnapshot(CallIceSnapshotFactory.From(selection));

            // Seed the running ICE transport state so a later consent loss reads as Connected → Disconnected.
            if (selection.HasSelectedPair)
                (call as Domain.Calls.Call)?.SetIceConnectionState(Domain.Calls.CallIceState.Connected);

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
                // Fail-closed (#165 P1-2): no validated pair. The caller must NOT fall back to the
                // unvalidated SDP endpoints, so signal "no media" rather than returning the raw parameters.
                return null;
            }

            var localRtcp = parameters.RtcpMux
                ? selection.LocalEndPoint
                : parameters.LocalRtcpEndPoint;
            var remoteRtcp = parameters.RtcpMux
                ? selection.RemoteEndPoint
                : parameters.RemoteRtcpEndPoint;

            // Carry every negotiated field across and override only the ICE-selected transport
            // endpoints. A hand-written copy here previously dropped the SDES/DTLS key material,
            // IceControlling, and Video, silently downgrading secure/video calls after ICE (HARD-R5).
            return parameters with
            {
                LocalEndPoint = selection.LocalEndPoint,
                RemoteEndPoint = selection.RemoteEndPoint,
                LocalRtcpEndPoint = localRtcp,
                RemoteRtcpEndPoint = remoteRtcp
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Lifecycle cancellation: the call terminated while ICE was selecting. Propagate so the caller
            // aborts quietly — it must neither install a session nor hang up an already-terminating call.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ICE selection failed for call {CallId}; failing the call closed.", callId);
            (call as Domain.Calls.Call)?.SetIceSnapshot(new CallIceSnapshot(
                CallIceState.Failed,
                HasSelectedPair: false,
                Nominated: false,
                LocalCandidate: null,
                RemoteCandidate: null,
                SelectedLocalEndPoint: null,
                SelectedRemoteEndPoint: null));
            // Fail-closed (#165 P1-2): an ICE failure must not silently downgrade media to the raw SDP endpoints.
            return null;
        }
    }

}
