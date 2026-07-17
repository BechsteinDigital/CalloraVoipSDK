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
using CalloraVoipSdk.Core.Domain.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Adapts one SIP core call session to the domain ICallChannel port.
/// Buffers signaling callbacks until domain callbacks are bound.
/// Fires <see cref="ICallChannel.MediaParametersNegotiated"/> once the SDP exchange
/// is complete so the application media orchestrator can set up RTP I/O.
/// </summary>
internal sealed partial class SipCoreCallChannel : ICallChannel
{
    private readonly ILogger<SipCoreCallChannel> _logger;
    private readonly ISdpNegotiator _sdpNegotiator;
    private readonly ICallIceAgent? _iceAgent;
    private readonly ISipTelemetrySink _telemetry;
    private readonly SipCallChannelSrtpTelemetry _srtpTelemetry;
    private readonly SipCallChannelSrtpPolicyGuard _srtpPolicyGuard;
    private readonly SrtpPolicy _appliedSrtpPolicy;
    private readonly string _srtpPolicySource;
    private readonly IReadOnlyList<string>? _preferredCodecNames;
    private readonly object _callbackSync = new();
    private readonly object _sessionSync = new();
    private readonly Queue<CallState> _stateBuffer = new();
    private readonly Queue<bool> _remoteHoldBuffer = new();

    // Encoded-media-frame taps (send delegate + inbound listener fan-out) — own collaborators so the
    // channel no longer carries the audio/video frame state and locks inline (were the AudioFrames
    // methods and the VideoFrames partial).
    private readonly SipCallChannelFrameTap<CallAudioFrame> _audioTap;
    private readonly SipCallChannelFrameTap<CallVideoFrame> _videoTap;

    // Pre-allocated local UDP socket so the port is known before the SDP offer is built.
    private readonly UdpClient _localMediaSocket;
    private readonly int _localMediaPort;

    // Video (WebRTC phase 2): a second reserved UDP socket/port for the m=video line,
    // present only when video is enabled. Released before the video RtpSession binds it,
    // exactly like the audio port-reservation socket.
    private readonly UdpClient? _localVideoSocket;
    private readonly int _localVideoPort;
    private readonly bool _videoEnabled;
    private readonly IReadOnlyList<string>? _videoCodecNames;

    private Action<CallState>? _onStateChange;
    private Action<byte, int>? _onDtmf;
    private Action<bool>? _onRemoteHold;
    private Func<string, string, bool>? _onTransfer;
    private Func<byte, int, CancellationToken, Task>? _dtmfSendDelegate;
    private CallIceLocalDescription? _localIceDescription;

    // ICE role for this leg (RFC 8445 §5.1.1): controlling when we send the SDP offer, controlled
    // when we answer. Set by the offer/answer entry points before media parameters are published.
    private volatile bool _iceControlling = true;
    private IPAddress? _advertisedMediaAddress;
    private readonly IPAddress? _configuredPublicMediaAddress;
    private ISipCallSession? _session;
    private int _mediaParametersFired;

    // The serialized answer SDP we sent (carries our own SDES crypto line when SRTP was
    // negotiated). Written on the answer path before the session establishes, read by the
    // media-parameter publication which may run on the signaling receive thread.
    private volatile string? _localAnswerSdp;

    // The serialized offer SDP we sent (outbound leg). Carries our own SDES crypto line
    // (the outbound encrypt key) when the SRTP policy makes us offer it; the peer's 200 OK
    // answer carries their key (inbound decrypt). Retained so media-parameter publication
    // recovers our key the same way the answer path does.
    private volatile string? _localOfferSdp;

    // The outbound encrypt key of the running SRTP media context (null when the call is
    // plain RTP). A hold/unhold re-offer reuses it so the offered a=crypto stays identical
    // to the live context — the peer keeps decrypting without a rekey. Set once media
    // parameters publish; read on the signaling thread that issues hold/unhold.
    private volatile string? _activeLocalSrtpKeyParams;

    // The live outbound SDES key for the video m-line (RFC 4568 per-m-line), re-advertised on a
    // hold/unhold re-offer so the running SRTP video stream is not rekeyed; null when the call
    // has no SDES-keyed video. Independent of the audio key above.
    private volatile string? _activeLocalVideoSrtpKeyParams;

    // DTLS-SRTP signaling (RFC 5763): local identity (fingerprint) plus whether locally
    // originated offers advertise DTLS keying. _dtlsActiveOnCall latches once a leg
    // negotiated DTLS so hold/unhold re-offers keep signaling it.
    private readonly SdpDtlsNegotiationOptions? _dtlsOptions;
    private readonly bool _offerDtlsSrtp;
    private volatile bool _dtlsActiveOnCall;

    // Signature of the last published media parameters (SRTP keys + remote endpoint + codec).
    // A re-INVITE whose negotiated media differs from this re-publishes MediaParametersNegotiated
    // (rekey → the orchestrator rebuilds the media session); an identical one (a retransmission)
    // does not, so media never churns without an actual change. Written on the signaling thread.
    private volatile string? _lastPublishedSignature;

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
        IReadOnlyList<string>? preferredCodecNames = null,
        IPAddress? advertisedPublicMediaAddress = null,
        SdpDtlsNegotiationOptions? dtlsOptions = null,
        bool offerDtlsSrtp = false,
        bool enableVideo = false,
        IReadOnlyList<string>? preferredVideoCodecNames = null)
    {
        _dtlsOptions = dtlsOptions;
        _offerDtlsSrtp = offerDtlsSrtp && dtlsOptions is not null;
        _videoEnabled = enableVideo;
        _videoCodecNames = preferredVideoCodecNames;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audioTap = new SipCallChannelFrameTap<CallAudioFrame>("Audio", _logger);
        _videoTap = new SipCallChannelFrameTap<CallVideoFrame>("Video", _logger);
        _sdpNegotiator = sdpNegotiator ?? throw new ArgumentNullException(nameof(sdpNegotiator));
        _iceAgent = iceAgent;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _appliedSrtpPolicy = appliedSrtpPolicy;
        _srtpPolicySource = string.IsNullOrWhiteSpace(policySource) ? "unknown" : policySource;
        _srtpTelemetry = new SipCallChannelSrtpTelemetry(_telemetry, appliedSrtpPolicy, _srtpPolicySource);
        _srtpPolicyGuard = new SipCallChannelSrtpPolicyGuard(appliedSrtpPolicy, _srtpTelemetry, _logger);
        _preferredCodecNames = preferredCodecNames;
        _configuredPublicMediaAddress = advertisedPublicMediaAddress;

        // Bind on any address, port 0 → OS assigns a free port.
        _localMediaSocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _localMediaPort = ((IPEndPoint)_localMediaSocket.Client.LocalEndPoint!).Port;

        if (_videoEnabled)
        {
            _localVideoSocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _localVideoPort = ((IPEndPoint)_localVideoSocket.Client.LocalEndPoint!).Port;
        }
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
        _iceControlling = true; // we send the offer → controlling (RFC 8445 §5.1.1)
        await EnsureLocalIceDescriptionAsync(localMediaEndPoint, ct).ConfigureAwait(false);

        // Offer DTLS-SRTP when configured (RFC 5763, mutually exclusive with SDES);
        // otherwise offer SDES SRTP unless policy forbids it (Disabled). Retain the exact
        // string we send: its a=crypto line is the outbound encrypt key the media layer
        // recovers, its a=fingerprint commits our DTLS identity.
        var offerSdp = _sdpNegotiator.BuildDefaultSdp(
            localMediaEndPoint,
            hold,
            BuildLocalSdpOptions(
                offerSrtpCrypto: _appliedSrtpPolicy != SrtpPolicy.Disabled && !_offerDtlsSrtp,
                // Disabled policy also disables DTLS offering — offering keying the policy
                // would then terminate on success is self-defeating.
                offerDtls: _offerDtlsSrtp && _appliedSrtpPolicy != SrtpPolicy.Disabled));
        _localOfferSdp = offerSdp;
        return offerSdp;
    }

    /// <inheritdoc />
    public string? RemoteAssertedIdentity
    {
        get { lock (_sessionSync) return _session?.RemoteAssertedIdentity; }
    }

    /// <inheritdoc />
    public string? Diversion
    {
        get { lock (_sessionSync) return _session?.Diversion; }
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

        _srtpTelemetry.PublishPolicyApplied(session);

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

        var state = SipCallChannelConversions.MapState(session.State);
        if (state is not null)
            NotifyState(state.Value);
    }

    /// <inheritdoc />
    public async Task AnswerAsync(CancellationToken ct)
    {
        var session = EnsureSession();
        _iceControlling = false; // we answer the peer's offer → controlled (RFC 8445 §5.1.1)

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

        if (!_srtpPolicyGuard.ValidateInboundOffer(session.RemoteSdp, out var inboundOfferReasonCode))
        {
            await _srtpPolicyGuard.RejectInboundAsync(
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
            await _srtpPolicyGuard.RejectInboundAsync(session, reasonCode, ct).ConfigureAwait(false);
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
        var holdSdp = _sdpNegotiator.BuildDefaultSdp(localEndPoint, hold: true, BuildReofferSdpOptions());
        await session.HoldAsync(holdSdp).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UnholdAsync()
    {
        var session = EnsureSession();
        var localIp = ResolveAdvertisedMediaAddress(session);
        var localEndPoint = new IPEndPoint(localIp, _localMediaPort);
        await EnsureLocalIceDescriptionAsync(localEndPoint, CancellationToken.None).ConfigureAwait(false);
        var unholdSdp = _sdpNegotiator.BuildDefaultSdp(localEndPoint, hold: false, BuildReofferSdpOptions());
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
            // candidate carries the real RTP port (a second bind would EADDRINUSE). When video is
            // enabled, gather a host candidate for the video 5-tuple too (its own port, shared
            // ufrag/pwd) so a peer can check the video stream (RFC 8839). The video candidate
            // reuses the same resolved advertised address as the audio host candidate (both from
            // `localEndPoint.Address`) — only the port differs.
            var videoLocalEndPoint = _videoEnabled
                ? new IPEndPoint(localEndPoint.Address, _localVideoPort)
                : null;
            _localIceDescription = await _iceAgent
                .BuildLocalDescriptionAsync(
                    localEndPoint, _localMediaSocket.Client, videoLocalEndPoint, _localVideoSocket?.Client, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to gather local ICE description for endpoint {LocalEndPoint}.", localEndPoint);
            _localIceDescription = null;
        }
    }

    /// <summary>
    /// Resolves the local media address to advertise in SDP and to bind RTP/RTCP on.
    /// Cached per channel so SDP and RTP always agree even on multi-homed hosts
    /// (a benign race may probe twice; both probes see the same routing table).
    /// See <see cref="AdvertisedMediaAddressResolver"/> for the decision rules.
    /// </summary>
    private IPAddress ResolveAdvertisedMediaAddress(ISipCallSession session) =>
        // Opt-in override wins: an operator behind CGNAT / static 1:1 NAT can force the public
        // media IP. Default (null) keeps the auto-resolved, symmetric-RTP-friendly address.
        _configuredPublicMediaAddress
        ?? (_advertisedMediaAddress ??= AdvertisedMediaAddressResolver.Resolve(
            session,
            AdvertisedMediaAddressResolver.ProbeRoute,
            _logger));

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
        var symbol = SipCallChannelConversions.ToDtmfSymbol(dtmfCode);
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

        // RFC 5589 attended transfer: REFER the transferee to the consultation target, carrying an
        // RFC 3891 Replaces that identifies the established consultation dialog. Falls back to a
        // plain REFER to the target URI when the consultation dialog has no tags yet.
        var referTo = AttendedTransferReferTo.Build(
            targetSession.CallId,
            targetSession.LocalTag,
            targetSession.RemoteTag,
            targetSession.RemoteUri)
            ?? targetSession.RemoteUri;

        return session.SendReferAsync(referTo, referredBy: session.LocalUri, ct: ct);
    }

    /// <inheritdoc />
    public Task SendAudioFrameAsync(CallAudioFrame frame, CancellationToken ct = default)
        => _audioTap.SendFrameAsync(frame, ct);

    /// <inheritdoc />
    public void DeliverInboundAudioFrame(CallAudioFrame frame) => _audioTap.DeliverInbound(frame);

    /// <inheritdoc />
    public void SetAudioSendDelegate(Func<CallAudioFrame, CancellationToken, Task>? sendDelegate)
        => _audioTap.SetSendDelegate(sendDelegate);

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
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipCoreCallChannel));
        _audioTap.AddListener(onFrame);
    }

    /// <inheritdoc />
    public void RemoveAudioFrameListener(Action<CallAudioFrame> onFrame) => _audioTap.RemoveListener(onFrame);

    // ── Video frames (delegated to the video tap) ─────────────────────────────

    /// <inheritdoc />
    public Task SendVideoFrameAsync(CallVideoFrame frame, CancellationToken ct = default)
        => _videoTap.SendFrameAsync(frame, ct);

    /// <inheritdoc />
    public void DeliverInboundVideoFrame(CallVideoFrame frame) => _videoTap.DeliverInbound(frame);

    /// <inheritdoc />
    public void SetVideoSendDelegate(Func<CallVideoFrame, CancellationToken, Task>? sendDelegate)
        => _videoTap.SetSendDelegate(sendDelegate);

    /// <inheritdoc />
    public void AddVideoFrameListener(Action<CallVideoFrame> onFrame)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipCoreCallChannel));
        _videoTap.AddListener(onFrame);
    }

    /// <inheritdoc />
    public void RemoveVideoFrameListener(Action<CallVideoFrame> onFrame) => _videoTap.RemoveListener(onFrame);

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
            _srtpTelemetry.PublishDecision(session, isSrtpNegotiated: false, profile: string.Empty, reasonCode, violatesPolicy: _appliedSrtpPolicy == SrtpPolicy.Required);
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
            _srtpTelemetry.PublishDecision(session, isSrtpNegotiated: false, profile: string.Empty, reasonCode, violatesPolicy: _appliedSrtpPolicy == SrtpPolicy.Required);
            return _appliedSrtpPolicy == SrtpPolicy.Required
                ? MediaPublicationResult.PolicyViolation
                : MediaPublicationResult.Skipped;
        }

        var withIceMetadata = CallMediaParametersIceEnricher.Enrich(parameters, _localIceDescription, _iceControlling);
        reasonCode = SrtpPolicyEvaluator.ResolveReasonCode(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        var violatesPolicy = SrtpPolicyEvaluator.IsPolicyViolation(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        var enrichedParameters = CallMediaParametersDtlsEnricher.Enrich(
            CallMediaParametersSrtpEnricher.Enrich(
                withIceMetadata, reasonCode, remoteSdp, _localAnswerSdp ?? _localOfferSdp, _appliedSrtpPolicy),
            remoteSdp, _localAnswerSdp ?? _localOfferSdp);

        // Fail closed on a keyless secure negotiation: the exchange signals SRTP (secure
        // profile / fingerprint) but produced neither SDES keys nor a DTLS association —
        // e.g. a UDP/TLS answer without a fingerprint. Under Required this is a policy
        // violation; the media layer additionally stays fail-closed (RequireEncryptedMedia).
        if (IsKeylessSecureNegotiation(enrichedParameters))
        {
            reasonCode = SrtpDecisionReasonCodes.RequiredNegotiationFailed;
            violatesPolicy = _appliedSrtpPolicy == SrtpPolicy.Required;
        }

        // Remember the live outbound encrypt key so a later hold/unhold re-offers the same
        // key (keeps SRTP without rekeying); null when the call resolved to plain RTP.
        // A DTLS-keyed leg latches instead so re-offers keep signaling DTLS.
        _activeLocalSrtpKeyParams = enrichedParameters.SrtpLocalKeyParams;
        _activeLocalVideoSrtpKeyParams = enrichedParameters.Video?.SrtpLocalKeyParams;
        _dtlsActiveOnCall = enrichedParameters.IsDtlsNegotiated;
        _srtpTelemetry.PublishDecision(
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

        // Release the port-reservation sockets so the audio and video RtpSessions can bind
        // the same ports. UdpClient.Dispose is idempotent; Dispose() below calls it again.
        try
        {
            _localMediaSocket.Dispose();
            _localVideoSocket?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Port-reservation socket already disposed when releasing for RtpSession.");
        }

        _lastPublishedSignature = RekeySignature(enrichedParameters);
        MediaParametersNegotiated?.Invoke(this, enrichedParameters);
        return MediaPublicationResult.Published;
    }

    /// <summary>
    /// Re-publishes media parameters when an established call's re-INVITE changes the negotiated
    /// media (RFC 3264 §8: new SDES key, remote endpoint, or codec). Additive to the initial
    /// <see cref="TryPublishMediaParameters"/>: it runs only after the first publish and only on a
    /// real change (a retransmission with an identical signature is ignored), so the orchestrator
    /// rebuilds the media session with the new keys only when something actually changed.
    /// </summary>
    private void TryRepublishMediaParametersOnRekey(ISipCallSession session)
    {
        var remoteSdp = session.RemoteSdp;
        if (string.IsNullOrWhiteSpace(remoteSdp))
            return;

        var localEndPoint = new IPEndPoint(ResolveAdvertisedMediaAddress(session), _localMediaPort);
        var parameters = _sdpNegotiator.TryParseMediaParameters(remoteSdp, localEndPoint, BuildSdpOptions());
        if (parameters is null)
            return;

        var withIceMetadata = CallMediaParametersIceEnricher.Enrich(parameters, _localIceDescription, _iceControlling);
        var reasonCode = SrtpPolicyEvaluator.ResolveReasonCode(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        // On an inbound re-INVITE the session carries the fresh answer we sent (new local key);
        // on the outbound leg it is null and we fall back to our retained answer/offer.
        var enriched = CallMediaParametersDtlsEnricher.Enrich(
            CallMediaParametersSrtpEnricher.Enrich(
                withIceMetadata, reasonCode, remoteSdp,
                session.LocalSdp ?? _localAnswerSdp ?? _localOfferSdp, _appliedSrtpPolicy),
            remoteSdp, session.LocalSdp ?? _localAnswerSdp ?? _localOfferSdp);

        if (string.Equals(RekeySignature(enriched), _lastPublishedSignature, StringComparison.Ordinal))
            return; // unchanged — retransmission or a re-INVITE that did not touch media

        var violatesPolicy = SrtpPolicyEvaluator.IsPolicyViolation(_appliedSrtpPolicy, withIceMetadata.IsSrtpNegotiated);
        if (IsKeylessSecureNegotiation(enriched))
        {
            // See TryPublishMediaParameters: a re-INVITE downgrading to a keyless secure
            // negotiation must not slip past the policy either.
            reasonCode = SrtpDecisionReasonCodes.RequiredNegotiationFailed;
            violatesPolicy = _appliedSrtpPolicy == SrtpPolicy.Required;
        }
        _srtpTelemetry.PublishDecision(session, enriched.IsSrtpNegotiated, enriched.MediaProfile, reasonCode, violatesPolicy);
        if (violatesPolicy)
        {
            _ = TerminateForSrtpPolicyViolationAsync(session, reasonCode);
            return;
        }

        _activeLocalSrtpKeyParams = enriched.SrtpLocalKeyParams;
        _activeLocalVideoSrtpKeyParams = enriched.Video?.SrtpLocalKeyParams;
        _dtlsActiveOnCall = enriched.IsDtlsNegotiated;
        _lastPublishedSignature = RekeySignature(enriched);
        _logger.LogDebug("Re-publishing media parameters on re-INVITE rekey for call {CallId}.", session.CallId);
        MediaParametersNegotiated?.Invoke(this, enriched);
    }

    /// <summary>
    /// True when the SDP exchange signals secure media but negotiated no usable keying:
    /// neither SDES key material nor a DTLS association. Such a leg must never run as
    /// plain RTP while reporting <c>IsSrtpNegotiated</c>.
    /// </summary>
    private static bool IsKeylessSecureNegotiation(CallMediaParameters p) =>
        p.IsSrtpNegotiated && p.SrtpLocalKeyParams is null && !p.IsDtlsNegotiated;

    /// <summary>Signature of the media-relevant parameters; equal signature = same media.</summary>
    private static string RekeySignature(CallMediaParameters p) =>
        $"{p.RemoteEndPoint}|{p.PayloadType}|{p.CodecName}|{p.MediaProfile}|{p.IsSrtpNegotiated}"
        + $"|{p.SrtpSuite}|{p.SrtpLocalKeyParams}|{p.SrtpRemoteKeyParams}"
        + $"|{p.IsDtlsNegotiated}|{p.DtlsIsClient}|{p.DtlsRemoteFingerprintAlgorithm}|{p.DtlsRemoteFingerprintValue}";

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
            if (Volatile.Read(ref _mediaParametersFired) == 0)
            {
                var result = TryPublishMediaParameters(s, out var reasonCode);
                if (result == MediaPublicationResult.PolicyViolation)
                {
                    _ = TerminateForSrtpPolicyViolationAsync(s, reasonCode);
                    return;
                }
            }
            else
            {
                // Already established once — a further Established transition is a re-INVITE
                // (e.g. unhold with a fresh peer key); rekey only when the media actually changed.
                TryRepublishMediaParametersOnRekey(s);
            }
        }

        var state = SipCallChannelConversions.MapState(e.NewState);
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
        _localVideoSocket?.Dispose();

        _audioTap.Dispose();
        _videoTap.Dispose();
        lock (_callbackSync)
        {
            _stateBuffer.Clear();
            _remoteHoldBuffer.Clear();
            _onStateChange = null;
            _onDtmf = null;
            _onRemoteHold = null;
            _onTransfer = null;
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
}
