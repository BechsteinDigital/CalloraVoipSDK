using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Adapts one SIP core call session to the domain ICallChannel port.
/// Buffers signaling callbacks until domain callbacks are bound.
/// Fires <see cref="ICallChannel.MediaParametersNegotiated"/> once the SDP exchange
/// is complete so the application media orchestrator can set up RTP I/O.
/// </summary>
internal sealed class SipCoreCallChannel : ICallChannel
{
    private readonly ILogger<SipCoreCallChannel> _logger;
    private readonly ISdpNegotiator _sdpNegotiator;
    private readonly ICallIceAgent? _iceAgent;
    private readonly ISipTelemetrySink _telemetry;
    private readonly SrtpPolicy _appliedSrtpPolicy;
    private readonly string _srtpPolicySource;
    private readonly IReadOnlyList<string>? _preferredCodecNames;
    private readonly object _callbackSync = new();
    private readonly object _sessionSync = new();
    private readonly object _audioSync = new();
    private readonly Queue<CallState> _stateBuffer = new();
    private readonly Queue<bool> _remoteHoldBuffer = new();
    private readonly List<Action<CallAudioFrame>> _audioListeners = [];

    // Pre-allocated local UDP socket so the port is known before the SDP offer is built.
    private readonly UdpClient _localMediaSocket;
    private readonly int _localMediaPort;

    private Action<CallState>? _onStateChange;
    private Action<byte, int>? _onDtmf;
    private Action<bool>? _onRemoteHold;
    private Func<string, string, bool>? _onTransfer;
    private Func<CallAudioFrame, CancellationToken, Task>? _audioSendDelegate;
    private Func<byte, int, CancellationToken, Task>? _dtmfSendDelegate;
    private CallIceLocalDescription? _localIceDescription;
    private IPAddress? _advertisedMediaAddress;
    private ISipCallSession? _session;
    private int _mediaParametersFired;

    // The serialized answer SDP we sent (carries our own SDES crypto line when SRTP was
    // negotiated). Written on the answer path before the session establishes, read by the
    // media-parameter publication which may run on the signaling receive thread.
    private volatile string? _localAnswerSdp;

    // RFC 4566 §5.2 origin identity. Each channel (call leg) gets a unique, stable session id;
    // the version is incremented on every locally built SDP so peers detect media changes.
    private static long _sessionIdSeed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly long _sdpSessionId = Interlocked.Increment(ref _sessionIdSeed);
    private long _sdpSessionVersion;

    private int _disposed;

    /// <inheritdoc />
    public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;

    /// <summary>
    /// Local UDP port allocated for RTP media. Available before AttachSession is called,
    /// so SipLineChannel can embed it in the SDP offer.
    /// </summary>
    internal int LocalMediaPort => _localMediaPort;

    /// <summary>
    /// Creates a channel adapter. Pre-allocates a local UDP socket to reserve a media port.
    /// </summary>
    internal SipCoreCallChannel(
        ILogger<SipCoreCallChannel> logger,
        ISdpNegotiator sdpNegotiator,
        ISipTelemetrySink telemetry,
        SrtpPolicy appliedSrtpPolicy,
        string policySource,
        ICallIceAgent? iceAgent = null,
        IReadOnlyList<string>? preferredCodecNames = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sdpNegotiator = sdpNegotiator ?? throw new ArgumentNullException(nameof(sdpNegotiator));
        _iceAgent = iceAgent;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _appliedSrtpPolicy = appliedSrtpPolicy;
        _srtpPolicySource = string.IsNullOrWhiteSpace(policySource) ? "unknown" : policySource;
        _preferredCodecNames = preferredCodecNames;

        // Bind on any address, port 0 → OS assigns a free port.
        _localMediaSocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _localMediaPort = ((IPEndPoint)_localMediaSocket.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Builds one outbound SDP offer and enriches it with local ICE credentials/candidates
    /// when ICE is enabled.
    /// </summary>
    internal async Task<string> BuildOfferSdpAsync(
        IPEndPoint localMediaEndPoint,
        bool hold,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(localMediaEndPoint);
        await EnsureLocalIceDescriptionAsync(localMediaEndPoint, ct).ConfigureAwait(false);
        return _sdpNegotiator.BuildDefaultSdp(localMediaEndPoint, hold, BuildLocalSdpOptions());
    }

    /// <summary>
    /// Attaches the SIP dialog session after INVITE bootstrap or inbound session creation.
    /// If the session is already Established (outbound), media negotiation is triggered immediately.
    /// </summary>
    internal void AttachSession(ISipCallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipCoreCallChannel));

        lock (_sessionSync)
        {
            if (_session is not null)
                throw new InvalidOperationException("SIP call session already attached.");

            _session = session;
            _session.StateChanged += HandleSessionStateChanged;
            _session.RemoteHoldChanged += HandleSessionRemoteHoldChanged;
            _session.DtmfReceived += HandleSessionDtmfReceived;
            _session.TransferRequested += HandleSessionTransferRequested;
        }

        PublishSrtpPolicyApplied(session);

        // For outbound calls the session is already Established here.
        // Fire MediaParametersNegotiated FIRST so Call.MediaParameters is populated
        // before the Connected state change reaches the application layer.
        if (session.State == SipDialogState.Established)
        {
            var result = TryPublishMediaParameters(session, out var reasonCode);
            if (result == MediaPublicationResult.PolicyViolation)
            {
                _ = TerminateForSrtpPolicyViolationAsync(session, reasonCode);
                return;
            }
        }

        var state = MapState(session.State);
        if (state is not null)
            NotifyState(state.Value);
    }

    /// <inheritdoc />
    public async Task AnswerAsync(CancellationToken ct)
    {
        var session = EnsureSession();

        // Route-local address for the SDP/bind. NAT reachability is handled by symmetric
        // RTP (the peer's SBC latches to our real source), so the connection line does not
        // need a STUN/public address — advertising one with the wrong RTP port breaks it.
        var localIp = ResolveAdvertisedMediaAddress(session);
        var localMediaEndPoint = new IPEndPoint(localIp, _localMediaPort);

        // ICE only when the offer actually included it (RFC 8445): advertising ICE
        // candidates unsolicited makes a non-ICE peer send STUN checks to our RTP port,
        // which pollutes the media path and blocks plain RTP.
        if (RemoteOfferHasIce(session.RemoteSdp))
            await EnsureLocalIceDescriptionAsync(localMediaEndPoint, ct).ConfigureAwait(false);

        if (!ValidateInboundOfferAgainstPolicy(session.RemoteSdp, out var inboundOfferReasonCode))
        {
            await RejectInboundOnSrtpPolicyViolationAsync(
                    session,
                    inboundOfferReasonCode,
                    ct)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Inbound offer violates SRTP policy '{_appliedSrtpPolicy}' ({inboundOfferReasonCode}).");
        }

        var localSdpOptions = BuildLocalSdpOptions();
        var answerSdp = string.IsNullOrWhiteSpace(session.RemoteSdp)
            ? _sdpNegotiator.BuildDefaultSdp(localMediaEndPoint, hold: false, localSdpOptions)
            : _sdpNegotiator.TryBuildNegotiatedAnswer(
                session.RemoteSdp,
                localMediaEndPoint,
                hold: false,
                localSdpOptions);

        if (string.IsNullOrWhiteSpace(answerSdp))
        {
            var reasonCode = _appliedSrtpPolicy == SrtpPolicy.Required
                ? SrtpDecisionReasonCodes.RequiredNegotiationFailed
                : SrtpDecisionReasonCodes.MediaParametersUnavailable;
            await RejectInboundOnSrtpPolicyViolationAsync(session, reasonCode, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Inbound SDP answer negotiation failed ({reasonCode}).");
        }

        // Retain the answer we send: it carries our own SDES crypto line (outbound
        // encrypt key), which the media layer recovers in TryPublishMediaParameters.
        _localAnswerSdp = answerSdp;

        await session.AnswerAsync(answerSdp, ct: ct).ConfigureAwait(false);

        // Fire media parameters so the orchestrator can start RTP.
        var result = TryPublishMediaParameters(session, out var reasonCodeAfterAnswer);
        if (result == MediaPublicationResult.PolicyViolation)
        {
            await TerminateForSrtpPolicyViolationAsync(session, reasonCodeAfterAnswer)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                $"SRTP policy violation after inbound answer ({reasonCodeAfterAnswer}).");
        }
    }

    /// <inheritdoc />
    public async Task HangupAsync()
    {
        var session = EnsureSession();
        await session.HangupAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HoldAsync()
    {
        var session = EnsureSession();
        var localIp = ResolveAdvertisedMediaAddress(session);
        var localEndPoint = new IPEndPoint(localIp, _localMediaPort);
        await EnsureLocalIceDescriptionAsync(localEndPoint, CancellationToken.None).ConfigureAwait(false);
        var holdSdp = _sdpNegotiator.BuildDefaultSdp(localEndPoint, hold: true, BuildLocalSdpOptions());
        await session.HoldAsync(holdSdp).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UnholdAsync()
    {
        var session = EnsureSession();
        var localIp = ResolveAdvertisedMediaAddress(session);
        var localEndPoint = new IPEndPoint(localIp, _localMediaPort);
        await EnsureLocalIceDescriptionAsync(localEndPoint, CancellationToken.None).ConfigureAwait(false);
        var unholdSdp = _sdpNegotiator.BuildDefaultSdp(localEndPoint, hold: false, BuildLocalSdpOptions());
        await session.UnholdAsync(unholdSdp).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures local ICE credentials/candidates are prepared before SDP generation.
    /// </summary>
    private async Task EnsureLocalIceDescriptionAsync(IPEndPoint localEndPoint, CancellationToken ct)
    {
        if (_iceAgent is null)
            return;

        if (_localIceDescription is not null)
            return;

        try
        {
            // Route STUN gathering through the reserved media socket so the srflx
            // candidate carries the real RTP port (a second bind would EADDRINUSE).
            _localIceDescription = await _iceAgent
                .BuildLocalDescriptionAsync(localEndPoint, _localMediaSocket.Client, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to gather local ICE description for endpoint {LocalEndPoint}.", localEndPoint);
            _localIceDescription = null;
        }
    }

    /// <summary>
    /// Creates optional SDP negotiation options from the currently gathered local ICE description.
    /// </summary>
    private SdpMediaNegotiationOptions? BuildSdpOptions()
    {
        if (_localIceDescription is null && _preferredCodecNames is null)
            return null;

        return new SdpMediaNegotiationOptions
        {
            Ice = _localIceDescription is null
                ? null
                : new SdpIceNegotiationOptions
                {
                    Ufrag = _localIceDescription.Ufrag,
                    Pwd = _localIceDescription.Pwd,
                    Options = _localIceDescription.Options,
                    Candidates = _localIceDescription.Candidates
                },
            PreferredCodecNames = _preferredCodecNames
        };
    }

    /// <summary>
    /// SDP options for a locally originated description (offer/answer/hold/unhold). Carries
    /// the stable per-leg origin session id and a session version incremented on every call —
    /// each represents a media change the peer must detect (RFC 4566 §5.2 / RFC 3264 §5).
    /// Retransmissions reuse the already-built SDP string and never call this, so the version
    /// stays put when nothing changed.
    /// </summary>
    private SdpMediaNegotiationOptions BuildLocalSdpOptions()
    {
        var baseOptions = BuildSdpOptions();
        return new SdpMediaNegotiationOptions
        {
            Ice = baseOptions?.Ice,
            PreferredCodecNames = baseOptions?.PreferredCodecNames ?? _preferredCodecNames,
            SessionId = _sdpSessionId,
            SessionVersion = Interlocked.Increment(ref _sdpSessionVersion)
        };
    }

    /// <summary>
    /// Resolves the local media address to advertise in SDP and to bind RTP/RTCP on.
    /// Cached per channel so SDP and RTP always agree even on multi-homed hosts
    /// (a benign race may probe twice; both probes see the same routing table).
    /// See <see cref="AdvertisedMediaAddressResolver"/> for the decision rules.
    /// </summary>
    private IPAddress ResolveAdvertisedMediaAddress(ISipCallSession session) =>
        _advertisedMediaAddress ??= AdvertisedMediaAddressResolver.Resolve(
            session,
            AdvertisedMediaAddressResolver.ProbeRoute,
            _logger);

    /// <summary>Returns true when the remote SDP offer signals ICE (a=ice-ufrag).</summary>
    private static bool RemoteOfferHasIce(string? remoteSdp) =>
        !string.IsNullOrWhiteSpace(remoteSdp)
        && remoteSdp.Contains("ice-ufrag", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task SendDtmfAsync(byte dtmfCode)
    {
        const int durationMs = 160;

        Func<byte, int, CancellationToken, Task>? rtpDtmfSender;
        lock (_callbackSync) rtpDtmfSender = _dtmfSendDelegate;

        if (rtpDtmfSender is not null)
        {
            try
            {
                await rtpDtmfSender(dtmfCode, durationMs, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "RTP DTMF sending failed for tone {ToneCode}; falling back to SIP INFO.",
                    dtmfCode);
            }
        }

        var session = EnsureSession();
        var symbol = ToDtmfSymbol(dtmfCode);
        var body = $"Signal={symbol}\r\nDuration={durationMs}\r\n";
        await session.SendInfoAsync("application/dtmf-relay", body).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RejectAsync(int statusCode, string? reasonPhrase, CancellationToken ct)
    {
        var session = EnsureSession();
        return session.RejectAsync(statusCode, reasonPhrase, ct);
    }

    /// <inheritdoc />
    public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode, CancellationToken ct)
    {
        var session = EnsureSession();
        return session.RedirectAsync(contactUris, statusCode, ct);
    }

    /// <inheritdoc />
    public Task SendInfoAsync(string contentType, string body, CancellationToken ct)
    {
        var session = EnsureSession();
        return session.SendInfoAsync(contentType, body, ct);
    }

    /// <inheritdoc />
    public Task<bool> SendOptionsAsync(CancellationToken ct)
    {
        var session = EnsureSession();
        return session.SendOptionsAsync(ct);
    }

    /// <inheritdoc />
    public Task<bool> SendSubscribeAsync(
        string eventType,
        int expiresSeconds,
        string? acceptHeader,
        string? body,
        CancellationToken ct)
    {
        var session = EnsureSession();
        return session.SendSubscribeAsync(eventType, expiresSeconds, acceptHeader, body, ct);
    }

    /// <inheritdoc />
    public Task<bool> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType,
        string? body,
        CancellationToken ct)
    {
        var session = EnsureSession();
        return session.SendNotifyAsync(eventType, subscriptionState, contentType, body, ct);
    }

    /// <inheritdoc />
    public Task<bool> BlindTransferAsync(string targetUri, TimeSpan timeout, CancellationToken ct)
    {
        var session = EnsureSession();
        return session.SendReferAsync(targetUri, referredBy: session.LocalUri, ct: ct);
    }

    /// <inheritdoc />
    public Task<bool> AttendedTransferAsync(ICallChannel target, TimeSpan timeout, CancellationToken ct)
    {
        var session = EnsureSession();
        if (target is not SipCoreCallChannel sipTarget)
            return Task.FromResult(false);

        var targetSession = sipTarget.EnsureSession();
        return session.SendReferAsync(targetSession.RemoteUri, referredBy: session.LocalUri, ct: ct);
    }

    /// <inheritdoc />
    public Task SendAudioFrameAsync(CallAudioFrame frame, CancellationToken ct = default)
    {
        Func<CallAudioFrame, CancellationToken, Task>? send;
        lock (_callbackSync) send = _audioSendDelegate;
        return send is not null ? send(frame, ct) : Task.CompletedTask;
    }

    /// <inheritdoc />
    public void DeliverInboundAudioFrame(CallAudioFrame frame)
    {
        Action<CallAudioFrame>[] listeners;
        lock (_audioSync) listeners = [.. _audioListeners];
        foreach (var listener in listeners)
        {
            try { listener(frame); }
            catch (Exception ex)
            {
                // Isolate one listener's fault from the others and the RTP/callback thread.
                _logger.LogDebug(ex, "Audio frame listener threw; continuing with the remaining listeners.");
            }
        }
    }

    /// <inheritdoc />
    public void SetAudioSendDelegate(Func<CallAudioFrame, CancellationToken, Task>? sendDelegate)
    {
        lock (_callbackSync) _audioSendDelegate = sendDelegate;
    }

    /// <inheritdoc />
    public void SetDtmfSendDelegate(Func<byte, int, CancellationToken, Task>? sendDelegate)
    {
        lock (_callbackSync) _dtmfSendDelegate = sendDelegate;
    }

    /// <inheritdoc />
    public void DeliverInboundDtmf(byte toneCode, int durationMs)
        => NotifyDtmf(toneCode, durationMs);

    /// <inheritdoc />
    public void BindCallbacks(CallChannelCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);

        List<CallState> pendingStates;
        List<bool> pendingRemoteHold;

        lock (_callbackSync)
        {
            _onStateChange = callbacks.OnStateChange;
            _onDtmf = callbacks.OnDtmf;
            _onRemoteHold = callbacks.OnRemoteHold;
            _onTransfer = callbacks.OnTransferRequested;

            pendingStates = _stateBuffer.ToList();
            pendingRemoteHold = _remoteHoldBuffer.ToList();
            _stateBuffer.Clear();
            _remoteHoldBuffer.Clear();
        }

        foreach (var state in pendingStates)
            callbacks.OnStateChange(state);

        if (callbacks.OnRemoteHold is null) return;
        foreach (var isOnHold in pendingRemoteHold)
            callbacks.OnRemoteHold(isOnHold);
    }

    /// <inheritdoc />
    public void AddAudioFrameListener(Action<CallAudioFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipCoreCallChannel));

        lock (_audioSync)
        {
            if (_audioListeners.Contains(onFrame)) return;
            _audioListeners.Add(onFrame);
        }
    }

    /// <inheritdoc />
    public void RemoveAudioFrameListener(Action<CallAudioFrame> onFrame)
    {
        if (onFrame == null) return;
        lock (_audioSync) _audioListeners.Remove(onFrame);
    }

    // ── Media parameters ──────────────────────────────────────────────────────

    /// <summary>
    /// Parses and publishes negotiated media parameters.
    /// Guarded so it fires at most once per session (re-INVITE will fire again).
    /// </summary>
    private MediaPublicationResult TryPublishMediaParameters(
        ISipCallSession session,
        out string reasonCode)
    {
        reasonCode = SrtpDecisionReasonCodes.NotEvaluated;

        // Allow re-fire on re-INVITE (reset guard on new session).
        if (Interlocked.Exchange(ref _mediaParametersFired, 1) != 0)
            return MediaPublicationResult.Published;

        var remoteSdp = session.RemoteSdp;
        if (string.IsNullOrWhiteSpace(remoteSdp))
        {
            _logger.LogWarning("No remote SDP available for call {CallId}; RTP will not start.", session.CallId);
            reasonCode = SrtpDecisionReasonCodes.MediaParametersUnavailable;
            PublishSrtpDecision(session, isSrtpNegotiated: false, profile: string.Empty, reasonCode, violatesPolicy: _appliedSrtpPolicy == SrtpPolicy.Required);
            return _appliedSrtpPolicy == SrtpPolicy.Required
                ? MediaPublicationResult.PolicyViolation
                : MediaPublicationResult.Skipped;
        }

        // Same address the SDP advertises: RTP/RTCP must bind where the peer sends to,
        // and a loopback/wildcard signaling bind is not routable for a LAN peer.
        var localIp = ResolveAdvertisedMediaAddress(session);
        var localEndPoint = new IPEndPoint(localIp, _localMediaPort);

        var parameters = _sdpNegotiator.TryParseMediaParameters(remoteSdp, localEndPoint, BuildSdpOptions());
        if (parameters is null)
        {
            _logger.LogWarning("Failed to parse remote SDP for call {CallId}; RTP will not start.", session.CallId);
            reasonCode = SrtpDecisionReasonCodes.MediaParametersUnavailable;
            PublishSrtpDecision(session, isSrtpNegotiated: false, profile: string.Empty, reasonCode, violatesPolicy: _appliedSrtpPolicy == SrtpPolicy.Required);
            return _appliedSrtpPolicy == SrtpPolicy.Required
                ? MediaPublicationResult.PolicyViolation
                : MediaPublicationResult.Skipped;
        }

        var withIceMetadata = EnrichWithIceMetadata(parameters);
        reasonCode = SrtpPolicyEvaluator.ResolveReasonCode(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        var violatesPolicy = SrtpPolicyEvaluator.IsPolicyViolation(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        var enrichedParameters = EnrichWithSrtpMetadata(withIceMetadata, reasonCode, remoteSdp);
        PublishSrtpDecision(
            session,
            enrichedParameters.IsSrtpNegotiated,
            enrichedParameters.MediaProfile,
            reasonCode,
            violatesPolicy);

        if (violatesPolicy)
        {
            _logger.LogWarning(
                "SRTP policy violation for call {CallId}: policy={Policy} profile={Profile} reason={ReasonCode}",
                session.CallId,
                _appliedSrtpPolicy,
                enrichedParameters.MediaProfile,
                reasonCode);
            return MediaPublicationResult.PolicyViolation;
        }

        _logger.LogDebug(
            "Firing MediaParametersNegotiated for call {CallId}: local={Local} remote={Remote} PT={PT}",
            session.CallId, enrichedParameters.LocalEndPoint, enrichedParameters.RemoteEndPoint, enrichedParameters.PayloadType);

        // Release the port-reservation socket so RtpSession can bind the same port.
        // UdpClient.Dispose is idempotent; Dispose() below will safely call it again.
        try
        {
            _localMediaSocket.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Port-reservation socket already disposed when releasing for RtpSession.");
        }

        MediaParametersNegotiated?.Invoke(this, enrichedParameters);
        return MediaPublicationResult.Published;
    }

    // ── Session event handlers ────────────────────────────────────────────────

    private void HandleSessionStateChanged(object? sender, SipDialogStateChangedEventArgs e)
    {
        // Guard: events may fire on a background thread after the channel is disposed.
        // The unsubscribe in Dispose() races with in-flight event deliveries, so an early
        // disposed check prevents null-reference exceptions and spurious state transitions.
        if (Volatile.Read(ref _disposed) != 0) return;

        // Keep ordering consistent with AttachSession(): publish media parameters
        // before Connected so call.MediaParameters is populated for app callbacks.
        if (e.NewState == SipDialogState.Established && sender is ISipCallSession s)
        {
            var result = TryPublishMediaParameters(s, out var reasonCode);
            if (result == MediaPublicationResult.PolicyViolation)
            {
                _ = TerminateForSrtpPolicyViolationAsync(s, reasonCode);
                return;
            }
        }

        var state = MapState(e.NewState);
        if (state is null) return;
        NotifyState(state.Value);
    }

    private void HandleSessionRemoteHoldChanged(object? sender, bool isOnHold)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        NotifyRemoteHold(isOnHold);
    }

    private void HandleSessionDtmfReceived(object? sender, SipDtmfReceivedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        NotifyDtmf(e.ToneCode, e.DurationMilliseconds);
    }

    private void HandleSessionTransferRequested(object? sender, SipTransferRequestedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        e.Accept = NotifyTransferRequested(e.ReferTo, e.ReferredBy);
    }

    // ── Notification helpers ──────────────────────────────────────────────────

    private void NotifyState(CallState state)
    {
        Action<CallState>? handler;
        lock (_callbackSync)
        {
            if (_onStateChange == null) { _stateBuffer.Enqueue(state); return; }
            handler = _onStateChange;
        }
        handler(state);
    }

    private void NotifyRemoteHold(bool isOnHold)
    {
        Action<bool>? handler;
        lock (_callbackSync)
        {
            if (_onRemoteHold == null) { _remoteHoldBuffer.Enqueue(isOnHold); return; }
            handler = _onRemoteHold;
        }
        handler(isOnHold);
    }

    private void NotifyDtmf(byte toneCode, int durationMs)
    {
        Action<byte, int>? handler;
        lock (_callbackSync) handler = _onDtmf;
        handler?.Invoke(toneCode, durationMs);
    }

    private bool NotifyTransferRequested(string referTo, string referredBy)
    {
        Func<string, string, bool>? handler;
        lock (_callbackSync) handler = _onTransfer;
        return handler?.Invoke(referTo, referredBy) ?? false;
    }

    /// <summary>
    /// Clones parsed remote media parameters and merges the local ICE description
    /// generated for this channel.
    /// </summary>
    private CallMediaParameters EnrichWithIceMetadata(CallMediaParameters parameters)
    {
        if (_localIceDescription is null)
            return parameters;

        var iceEnabled = parameters.IceEnabled
                         || !string.IsNullOrWhiteSpace(parameters.RemoteIceUfrag)
                         || !string.IsNullOrWhiteSpace(parameters.RemoteIcePwd)
                         || parameters.RemoteIceCandidates.Count > 0;

        return new CallMediaParameters
        {
            LocalEndPoint = parameters.LocalEndPoint,
            RemoteEndPoint = parameters.RemoteEndPoint,
            RtcpMux = parameters.RtcpMux,
            LocalRtcpEndPoint = parameters.LocalRtcpEndPoint,
            RemoteRtcpEndPoint = parameters.RemoteRtcpEndPoint,
            PayloadType = parameters.PayloadType,
            CodecName = parameters.CodecName,
            PayloadTypeCodecMap = parameters.PayloadTypeCodecMap,
            TelephoneEventPayloadType = parameters.TelephoneEventPayloadType,
            ClockRate = parameters.ClockRate,
            SamplesPerPacket = parameters.SamplesPerPacket,
            MediaProfile = parameters.MediaProfile,
            IsSrtpNegotiated = parameters.IsSrtpNegotiated,
            IceEnabled = iceEnabled,
            LocalIceUfrag = _localIceDescription.Ufrag,
            LocalIcePwd = _localIceDescription.Pwd,
            LocalIceOptions = _localIceDescription.Options,
            LocalIceCandidates = _localIceDescription.Candidates,
            RemoteIceUfrag = parameters.RemoteIceUfrag,
            RemoteIcePwd = parameters.RemoteIcePwd,
            RemoteIceOptions = parameters.RemoteIceOptions,
            RemoteIceCandidates = parameters.RemoteIceCandidates,
            RemoteIceEndOfCandidates = parameters.RemoteIceEndOfCandidates
        };
    }

    /// <summary>
    /// Clones media parameters and stamps SRTP policy metadata for public consumption.
    /// </summary>
    private CallMediaParameters EnrichWithSrtpMetadata(
        CallMediaParameters parameters,
        string reasonCode,
        string remoteSdp)
    {
        // Recover SDES key material from the SDP that actually went over the wire:
        // the peer's SDP carries their key (inbound decrypt), our answer carries the
        // key we generated (outbound encrypt). Encryption only engages when both are
        // present and agree on the suite — never half-encrypted.
        var remoteCrypto = SdpUtilities.TryExtractAudioCrypto(remoteSdp);
        var localCrypto = SdpUtilities.TryExtractAudioCrypto(_localAnswerSdp);
        var sdesUsable = remoteCrypto is not null
            && localCrypto is not null
            && string.Equals(remoteCrypto.CryptoSuite, localCrypto.CryptoSuite, StringComparison.Ordinal);

        return new()
        {
            LocalEndPoint = parameters.LocalEndPoint,
            RemoteEndPoint = parameters.RemoteEndPoint,
            RtcpMux = parameters.RtcpMux,
            LocalRtcpEndPoint = parameters.LocalRtcpEndPoint,
            RemoteRtcpEndPoint = parameters.RemoteRtcpEndPoint,
            PayloadType = parameters.PayloadType,
            CodecName = parameters.CodecName,
            PayloadTypeCodecMap = parameters.PayloadTypeCodecMap,
            TelephoneEventPayloadType = parameters.TelephoneEventPayloadType,
            ClockRate = parameters.ClockRate,
            SamplesPerPacket = parameters.SamplesPerPacket,
            MediaProfile = parameters.MediaProfile,
            IsSrtpNegotiated = parameters.IsSrtpNegotiated,
            IceEnabled = parameters.IceEnabled,
            LocalIceUfrag = parameters.LocalIceUfrag,
            LocalIcePwd = parameters.LocalIcePwd,
            LocalIceOptions = parameters.LocalIceOptions,
            LocalIceCandidates = parameters.LocalIceCandidates,
            RemoteIceUfrag = parameters.RemoteIceUfrag,
            RemoteIcePwd = parameters.RemoteIcePwd,
            RemoteIceOptions = parameters.RemoteIceOptions,
            RemoteIceCandidates = parameters.RemoteIceCandidates,
            RemoteIceEndOfCandidates = parameters.RemoteIceEndOfCandidates,
            AppliedSrtpPolicy = _appliedSrtpPolicy,
            SrtpDecisionReasonCode = reasonCode,
            SrtpSuite = sdesUsable ? remoteCrypto!.CryptoSuite : null,
            SrtpLocalKeyParams = sdesUsable ? localCrypto!.KeyParams : null,
            SrtpRemoteKeyParams = sdesUsable ? remoteCrypto!.KeyParams : null
        };
    }

    /// <summary>
    /// Validates inbound offer security against the applied SRTP policy before sending 200 OK.
    /// </summary>
    private bool ValidateInboundOfferAgainstPolicy(string? remoteSdp, out string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(remoteSdp))
        {
            reasonCode = _appliedSrtpPolicy == SrtpPolicy.Required
                ? SrtpDecisionReasonCodes.RequiredRemoteNoSrtp
                : SrtpDecisionReasonCodes.NotEvaluated;
            return _appliedSrtpPolicy != SrtpPolicy.Required;
        }

        if (!SdpSecurityInspector.TryInspectAudioSecurity(remoteSdp, out var isSrtpSignaled, out _))
        {
            reasonCode = _appliedSrtpPolicy == SrtpPolicy.Required
                ? SrtpDecisionReasonCodes.RequiredNegotiationFailed
                : SrtpDecisionReasonCodes.NotEvaluated;
            return _appliedSrtpPolicy != SrtpPolicy.Required;
        }

        reasonCode = SrtpPolicyEvaluator.ResolveReasonCode(_appliedSrtpPolicy, isSrtpSignaled);
        return !SrtpPolicyEvaluator.IsPolicyViolation(_appliedSrtpPolicy, isSrtpSignaled);
    }

    /// <summary>
    /// Rejects an inbound INVITE when SRTP policy validation fails.
    /// </summary>
    private async Task RejectInboundOnSrtpPolicyViolationAsync(
        ISipCallSession session,
        string reasonCode,
        CancellationToken ct)
    {
        var inspected = SdpSecurityInspector.TryInspectAudioSecurity(
            session.RemoteSdp,
            out var isSrtpSignaled,
            out var profile);
        PublishSrtpDecision(
            session,
            inspected && isSrtpSignaled,
            inspected ? profile : string.Empty,
            reasonCode,
            violatesPolicy: true);

        try
        {
            await session.RejectAsync(488, "Not Acceptable Here", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed sending SRTP policy rejection for call {CallId}.",
                session.CallId);
        }
    }

    /// <summary>
    /// Terminates an already established dialog when negotiated media violates SRTP policy.
    /// </summary>
    private async Task TerminateForSrtpPolicyViolationAsync(
        ISipCallSession session,
        string reasonCode)
    {
        _logger.LogWarning(
            "Terminating call {CallId} due to SRTP policy violation: policy={Policy} reason={ReasonCode}",
            session.CallId,
            _appliedSrtpPolicy,
            reasonCode);

        try
        {
            await session.HangupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed sending BYE while terminating call {CallId} on SRTP policy violation.",
                session.CallId);
        }

        NotifyState(CallState.Terminated);
    }

    /// <summary>
    /// Emits one telemetry event indicating the resolved SRTP policy for this dialog.
    /// </summary>
    private void PublishSrtpPolicyApplied(ISipCallSession session)
    {
        _telemetry.PublishEvent(new SipEventRecord
        {
            EventType = "sip.media.srtp.policy.applied",
            CallId = session.CallId,
            CorrelationId = $"{session.CallId}:SRTP:POLICY",
            Attributes = new Dictionary<string, string>
            {
                ["policy"] = _appliedSrtpPolicy.ToString(),
                ["policy_source"] = _srtpPolicySource
            }
        });
    }

    /// <summary>
    /// Emits one telemetry decision record for SRTP negotiation outcome.
    /// </summary>
    private void PublishSrtpDecision(
        ISipCallSession session,
        bool isSrtpNegotiated,
        string profile,
        string reasonCode,
        bool violatesPolicy)
    {
        _telemetry.PublishEvent(new SipEventRecord
        {
            EventType = "sip.media.srtp.decision",
            CallId = session.CallId,
            CorrelationId = $"{session.CallId}:SRTP:DECISION",
            Attributes = new Dictionary<string, string>
            {
                ["policy"] = _appliedSrtpPolicy.ToString(),
                ["policy_source"] = _srtpPolicySource,
                ["negotiated"] = isSrtpNegotiated ? "true" : "false",
                ["violates_policy"] = violatesPolicy ? "true" : "false",
                ["reason_code"] = reasonCode,
                ["media_profile"] = profile
            }
        });
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        ISipCallSession? session;
        lock (_sessionSync)
        {
            session = _session;
            _session = null;
        }

        if (session is not null)
        {
            session.StateChanged -= HandleSessionStateChanged;
            session.RemoteHoldChanged -= HandleSessionRemoteHoldChanged;
            session.DtmfReceived -= HandleSessionDtmfReceived;
            session.TransferRequested -= HandleSessionTransferRequested;
            try { session.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to dispose SIP call session.");
            }
        }

        _localMediaSocket.Dispose();

        lock (_audioSync) _audioListeners.Clear();
        lock (_callbackSync)
        {
            _stateBuffer.Clear();
            _remoteHoldBuffer.Clear();
            _onStateChange = null;
            _onDtmf = null;
            _onRemoteHold = null;
            _onTransfer = null;
            _audioSendDelegate = null;
            _dtmfSendDelegate = null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private enum MediaPublicationResult
    {
        Published,
        Skipped,
        PolicyViolation
    }

    private ISipCallSession EnsureSession()
    {
        var session = Volatile.Read(ref _session);
        if (session is null)
            throw new InvalidOperationException("SIP call session has not been attached yet.");
        return session;
    }

    private static CallState? MapState(SipDialogState state) => state switch
    {
        SipDialogState.Inviting     => CallState.Dialing,
        SipDialogState.Ringing      => CallState.Ringing,
        SipDialogState.Established  => CallState.Connected,
        SipDialogState.OnHold       => CallState.OnHold,
        SipDialogState.Terminated   => CallState.Terminated,
        _                           => null
    };

    private static char ToDtmfSymbol(byte toneCode) => toneCode switch
    {
        <= 9 => (char)('0' + toneCode),
        10   => '*',
        11   => '#',
        12   => 'A',
        13   => 'B',
        14   => 'C',
        15   => 'D',
        _    => throw new ArgumentOutOfRangeException(nameof(toneCode))
    };
}
