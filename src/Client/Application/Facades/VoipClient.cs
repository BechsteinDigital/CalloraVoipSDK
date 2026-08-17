using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Convenience;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Application.Ports.Video;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Application.Ports.Media;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Infrastructure.Audio;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Media;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Application.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk;

/// <summary>
/// Public SDK facade composing line management, call management, and media routing.
/// This is the primary entry point for SDK consumers.
/// </summary>
public sealed class VoipClient : IVoipClient
{
    private readonly ISipTransportRuntime _transportRuntime;
    private readonly ISipRegistrationService _registrationService;
    private readonly ISipCallSignalingService _callSignalingService;
    private readonly bool _ownsRegistrationService;
    private readonly bool _ownsCallSignalingService;
    private readonly IAudioDevice _audioDevice;
    private readonly IAudioDeviceRuntimeControl? _audioDeviceRuntimeControl;
    private readonly bool _ownsAudioDevice;
    private readonly ILogger<VoipClient> _logger;
    private readonly CallMediaOrchestrator _mediaOrchestrator;
    private readonly SdkConvenienceOrchestrator _convenienceOrchestrator;
    // Per-line TLS override bound at the connect boundary (#183). Keyed by the SipAccount instance the
    // caller passes to ConnectAsync, read by the line factory when the line is created. Kept in the
    // Application layer so TlsConfiguration never crosses into the Domain PhoneLineManager (rule R1).
    private readonly ConcurrentDictionary<SipAccount, TlsConfiguration> _lineTlsByAccount =
        new(ReferenceEqualityComparer.Instance);
    // Started/stopping/stopped, so an aborted shutdown stays resumable (#166 P2-9).
    private readonly RuntimeLifecycleState _runtime = new();
    private int _disposed;

    /// <summary>
    /// Active call manager for this SDK instance.
    /// </summary>
    public ICallManager Calls { get; }

    /// <summary>
    /// Registered line manager for this SDK instance.
    /// </summary>
    public IPhoneLineManager Lines { get; }

    /// <summary>
    /// Media manager for sender/receiver/connector orchestration.
    /// </summary>
    public IMediaManager Media { get; }

    /// <summary>
    /// Playback module facade.
    /// </summary>
    public IPlaybackModule PlaybackManager => ModuleManager.Playback;

    /// <summary>
    /// Recording module facade.
    /// </summary>
    public IRecordingModule RecordingManager => ModuleManager.Recording;

    /// <summary>
    /// Module availability facade.
    /// </summary>
    public IModuleManager ModuleManager { get; }

    /// <summary>
    /// Registry resolving optional modules contributed by separate packages.
    /// </summary>
    public IModuleRegistry Modules { get; }

    /// <summary>
    /// Runtime session view facade.
    /// </summary>
    public ISessionManager SessionManager { get; }

    /// <summary>
    /// Runtime audio-device facade.
    /// </summary>
    public IDeviceManager DeviceManager { get; }

    /// <summary>
    /// Runtime quality facade.
    /// </summary>
    public IQualityManager QualityManager { get; }

    /// <summary>
    /// Runtime policy facade.
    /// </summary>
    public IPolicyManager PolicyManager { get; }

    /// <summary>
    /// Runtime telemetry facade.
    /// </summary>
    public ITelemetryManager TelemetryManager { get; }

    /// <summary>
    /// Raised when a new inbound call arrives on any registered line. Fires on the SDK's SIP
    /// signaling thread; the handler must not block or throw (see <see cref="ICall"/> remarks for
    /// the full event-threading contract).
    /// </summary>
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <summary>
    /// Raised when an inbound SIP MESSAGE (RFC 3428 pager-mode instant message) arrives on any registered
    /// line. The SDK has already answered it 200 OK. Fires on the SDK's SIP signaling thread; the handler
    /// must not block or throw (see <see cref="ICall"/> remarks).
    /// </summary>
    public event EventHandler<IncomingMessageEventArgs>? IncomingMessage;

    /// <summary>
    /// Raised when any active call changes state. Fires on the SDK's SIP signaling thread; the
    /// handler must not block or throw (see <see cref="ICall"/> remarks).
    /// </summary>
    public event EventHandler<CallStateChangedEventArgs>? CallStateChanged;

    /// <summary>
    /// Creates a new SDK client using explicit configuration and optional DI service overrides.
    /// </summary>
    public VoipClient(VoipConfiguration? config = null)
        : this(config, null)
    {
    }

    internal VoipClient(VoipConfiguration? config, IServiceProvider? services)
    {
        config ??= new VoipConfiguration();
#pragma warning disable CS0618
        services ??= config.Services;
#pragma warning restore CS0618
        var logFactory = config.LoggerFactory
            ?? ResolveService<ILoggerFactory>(services)
            ?? NullLoggerFactory.Instance;
        _logger = logFactory.CreateLogger<VoipClient>();

        // Any failure after this point (transport bind, DTLS identity, ICE agent, line-manager factory,
        // module attach) leaks the resources already built — open SIP sockets, the owned audio device,
        // orchestrators. Wrapping the whole construction disposes whatever was built and rethrows the
        // original error. Dispose() is null-tolerant, so a throw mid-init still tears down cleanly. C#
        // permits assigning the readonly fields inside this try because it is still the constructor body.
        try
        {
            var injectedAudioDevice = ResolveService<IAudioDevice>(services);
            if (ReferenceEquals(config.AudioDevice, SilenceAudioDevice.Instance)
                && injectedAudioDevice is not null)
            {
                _audioDevice = injectedAudioDevice;
                _ownsAudioDevice = false;
            }
            else
            {
                _audioDevice = PlatformAudioDeviceFactory.Resolve(
                    config.AudioDevice,
                    config.EnableAutomaticAudioDeviceSelection,
                    _logger,
                    out _ownsAudioDevice);
            }

            _audioDeviceRuntimeControl = _audioDevice as IAudioDeviceRuntimeControl;

            var transportFactory = ResolveService<ISipTransportFactory>(services)
                ?? new SipTransportFactory();
            try
            {
                _transportRuntime = transportFactory.Create(
                    config.Tls,
                    logFactory,
                    MapTransport(config.DefaultTransport),
                    config.SipTransportHardening.ToTransportOptions(
                        config.LocalSipPort, config.LocalSipTlsPort));
            }
            catch (Exception ex) when (IsTransportInitializationFailure(ex))
            {
                throw new VoipClientInitializationException(
                    "SIP transport initialization failed. Check local socket permissions and SIP listener binding configuration.",
                    ex);
            }

            var digestAuthenticator = ResolveService<ISipDigestAuthenticator>(services)
                ?? new SipDigestAuthentication();
            var telemetry = new ClientTelemetrySink(
                ResolveService<ISipTelemetrySink>(services)
                ?? NullSipTelemetrySink.Instance,
                _logger);
            TelemetryManager = new TelemetryManager(telemetry, logFactory.CreateLogger<TelemetryManager>());

            var resolvedRegistrationService = ResolveService<ISipRegistrationService>(services);
            if (resolvedRegistrationService is null)
            {
                _registrationService = new SipRegistrationService(
                    _transportRuntime,
                    digestAuthenticator,
                    logFactory,
                    telemetry);
                _ownsRegistrationService = true;
            }
            else
            {
                _registrationService = resolvedRegistrationService;
                _ownsRegistrationService = false;
            }

            var sdpNegotiator = ResolveService<ISdpNegotiator>(services)
                ?? new SdpNegotiator(logFactory.CreateLogger<SdpNegotiator>());
            var sdpProvider = new SipSessionSdpProvider
            {
                BuildOffer = (ep, hold) => sdpNegotiator.BuildDefaultSdp(ep, hold, null),
                TryNegotiateAnswer = (offer, ep, hold) =>
                    offer is null ? null : sdpNegotiator.TryBuildNegotiatedAnswer(offer, ep, hold, null),
                TryParseMediaParameters = sdpNegotiator.TryParseMediaParameters,
                IsRemoteHold = sdpNegotiator.IsRemoteHoldSdp,
            };

            ICallIceAgent? iceAgent = ResolveService<ICallIceAgent>(services);
            if (iceAgent is null && config.Ice.Enabled)
            {
                var stunClient = ResolveService<IStunClient>(services)
                    ?? new StunClient(
                        new StunMessageCodec(),
                        logFactory.CreateLogger<StunClient>());
                var stunProbe = ResolveService<IIceStunProbe>(services)
                    ?? new StunIceProbe(stunClient, logFactory);
                var turnRelayAllocator = ResolveService<IIceTurnRelayAllocator>(services);
                var iceTelemetry = ResolveService<IIceTelemetrySink>(services)
                    ?? new SipIceTelemetrySink(telemetry);
                if (turnRelayAllocator is null
                    && config.Ice.Servers.Any(static s => s.Type == IceServerType.Turn))
                {
                    var turnClient = ResolveService<ITurnClient>(services)
                        ?? new TurnClient(
                            new StunMessageCodec(),
                            logFactory.CreateLogger<TurnClient>());
                    turnRelayAllocator = new TurnIceRelayAllocator(turnClient, logFactory);
                }

                iceAgent = new CallIceAgent(config.Ice, stunProbe, logFactory, turnRelayAllocator, iceTelemetry);
            }

            var resolvedCallSignalingService = ResolveService<ISipCallSignalingService>(services);
            if (resolvedCallSignalingService is null)
            {
                _callSignalingService = new SipCallSignalingService(
                    _transportRuntime,
                    digestAuthenticator,
                    logFactory,
                    sdpProvider,
                    telemetry,
                    inboundUserAgent: config.UserAgent,
                    maxConcurrentInboundSessions: config.SipSignalingHardening.MaxConcurrentInboundSessions,
                    inboundRingDeadline: config.SipSignalingHardening.InboundRingDeadline,
                    maxInboundSessionsPerRemote: config.SipSignalingHardening.MaxInboundSessionsPerRemote,
                    maxServerTransactions: config.SipSignalingHardening.MaxServerTransactions,
                    absoluteServerTransactionLifetime: config.SipSignalingHardening.AbsoluteServerTransactionLifetime);
                _ownsCallSignalingService = true;
            }
            else
            {
                _callSignalingService = resolvedCallSignalingService;
                _ownsCallSignalingService = false;
            }

            // Managers are exposed through interfaces (HARD-E5); construction keeps concrete locals where a
            // manager's constructor requires the concrete peer type.
            var callManager = new CallManager(logFactory.CreateLogger<CallManager>());
            Calls = callManager;
            // Re-raise with the facade as sender, not the internal manager, so subscribers to
            // VoipClient.CallStateChanged see the VoipClient they subscribed on (#18.9). Raised through the
            // shared dispatch (#166 P3-14): a throwing app handler is isolated per subscriber and never
            // reaches the SIP path this fires from.
            callManager.CallStateChanged += (_, e) =>
                SdkEventDispatch.Raise(CallStateChanged, this, e, _logger, nameof(CallStateChanged));

            var audioFileCodecs = ResolveService<IAudioFileCodecRegistry>(services)
                ?? new AudioFileCodecRegistry(logFactory);
            var mediaManager = new MediaManager(logFactory, audioFileCodecs);
            Media = mediaManager;
            var moduleManager = new ModuleManager(mediaManager);
            ModuleManager = moduleManager;
            SessionManager = new SessionManager(callManager, moduleManager.Playback, moduleManager.Recording);
            DeviceManager = new DeviceManager(GetAudioDeviceRuntimeControl, ThrowIfDisposed);
            QualityManager = new QualityManager();
            PolicyManager = new PolicyManager(config.SrtpPolicy);

            // Application media orchestrator: creates and manages RTP sessions per call.
            var bridgeTapCodec = config.BridgeAudioFormat == BridgeAudioFormat.Pcmu
                ? PayloadCodecKind.Pcmu
                : (PayloadCodecKind?)null;
            // DTLS-SRTP identity (RFC 5763): one ephemeral certificate per client instance.
            // Its fingerprint is signaled via SDP a=fingerprint (answers always, offers when
            // OfferDtlsSrtp is set); the handshaker keys DTLS-negotiated call legs.
            var dtlsHandshaker = ResolveService<IDtlsSrtpHandshaker>(services)
                ?? new DtlsSrtpHandshaker(logFactory.CreateLogger<DtlsSrtpHandshaker>());
            // Precedence: an internally DI-registered certificate, then a caller-supplied one from config
            // (HARD-E7, opt-in stable identity), else a fresh ephemeral ECDSA P-256 (WebRTC privacy default).
            var dtlsCertificate = ResolveService<DtlsCertificate>(services)
                ?? (config.DtlsCertificate is { } suppliedDtlsCertificate
                    ? DtlsCertificate.FromX509(suppliedDtlsCertificate)
                    : DtlsCertificate.GenerateEcdsaP256());
            var dtlsSignalingOptions = new SdpDtlsNegotiationOptions
            {
                FingerprintAlgorithm = dtlsCertificate.Fingerprint.Algorithm,
                FingerprintValue = dtlsCertificate.Fingerprint.Value,
            };
            var mediaSessionFactory = ResolveService<ICallMediaSessionFactory>(services)
                ?? new RtpCallMediaSessionFactory(logFactory, bridgeTapCodec, dtlsHandshaker, dtlsCertificate);
            var rtcpPacketCodec = ResolveService<IRtcpPacketCodec>(services)
                ?? new RtcpPacketCodec();
            var mediaSupervision = new MediaSupervisionOptions
            {
                InboundMediaTimeout = config.InboundMediaTimeout,
                HangupHeldCallOnSilence = config.HangupHeldCallOnMediaSilence
            };
            _mediaOrchestrator = new CallMediaOrchestrator(
                mediaSessionFactory, logFactory, rtcpPacketCodec, iceAgent, mediaSupervision);
            Calls.CallStateChanged += _mediaOrchestrator.OnCallStateChanged;

            var lineManager = new PhoneLineManager(account =>
            {
                var channel = new SipLineChannel(
                    account,
                    config.UserAgent,
                    _registrationService,
                    _callSignalingService,
                    sdpNegotiator,
                    iceAgent,
                    config.SrtpPolicy,
                    telemetry,
                    logFactory,
                    config.PreferredAudioCodecs,
                    dtlsSignalingOptions,
                    config.OfferDtlsSrtp,
                    config.EnableVideo,
                    config.PreferredVideoCodecs,
                    config.RequireSecureSignalingForSdes,
                    _lineTlsByAccount.GetValueOrDefault(account));

                return new PhoneLine(
                    account,
                    channel,
                    callManager,
                    config.MaxConcurrentCallsPerLine,
                    logFactory,
                    onCallCreated: (call, callChannel) =>
                        _mediaOrchestrator.AttachCall(call, callChannel));
            }, logFactory.CreateLogger<PhoneLineManager>());
            Lines = lineManager;

            // Facade as sender (see CallStateChanged above), not the inner line manager (#18.9); same shared
            // event dispatch, so an app handler cannot break the inbound INVITE/MESSAGE path (#166 P3-14).
            Lines.IncomingCall += (_, e) =>
                SdkEventDispatch.Raise(IncomingCall, this, e, _logger, nameof(IncomingCall));
            Lines.IncomingMessage += (_, e) =>
                SdkEventDispatch.Raise(IncomingMessage, this, e, _logger, nameof(IncomingMessage));

            // Video is transport-only: the SDK ships no codec, so the video device is optional and resolved
            // purely from DI (no platform-factory fallback like audio). When absent, AttachDefaultVideoAsync
            // fails closed. The application registers an IVideoDevice (its codec package) to enable it.
            var injectedVideoDevice = ResolveService<IVideoDevice>(services);
            _convenienceOrchestrator = new SdkConvenienceOrchestrator(
                lineManager, mediaManager, _audioDevice, logFactory, injectedVideoDevice);

            // Module registration is the last construction step so OnAttached sees a fully built client.
            Modules = new ModuleRegistry(this);
            var injectedModules = ResolveService<IEnumerable<IVoipClientModule>>(services);
            if (injectedModules is not null)
            {
                foreach (var module in injectedModules)
                {
                    Modules.Register(module);
                }
            }
        }
        catch
        {
            // Any construction step failed (transport, DTLS, ICE, line factory, third-party module
            // attach): release whatever was already built, then surface the original error unchanged.
            Dispose();
            throw;
        }
    }

    internal Task StartRuntimeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (_runtime.TryStart())
        {
            _logger.LogInformation("CalloraVoipSdk runtime started.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Hangs up the active calls and unregisters the registered lines. Resumable: if the shutdown is cancelled
    /// or a teardown step throws, the runtime stays marked as started, so calling it again resumes the teardown
    /// from what is still up instead of returning immediately (#166 P2-9). A no-op once it has completed.
    /// </summary>
    internal async Task StopRuntimeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        // Claim the shutdown. Nothing to do when the runtime never started, already stopped, or another
        // shutdown is in flight and owns the teardown.
        if (!_runtime.TryBeginShutdown())
        {
            return;
        }

        try
        {
            foreach (var call in Calls.Active)
            {
                if (call.State == CallState.Terminated)
                {
                    continue;
                }

                try
                {
                    await call.HangupAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hangup failed during runtime shutdown for call {CallId}.", call.CallId);
                }
            }

            foreach (var line in Lines.All.ToList())
            {
                try
                {
                    await Lines.UnregisterAsync(line.LineId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unregister failed during runtime shutdown for line {LineId}.", line.LineId);
                }
            }
        }
        catch
        {
            // Give the shutdown back so it stays resumable: a cancelled host stop must not leave a runtime
            // marked as stopped while calls are still up and lines are still registered. Both loops re-read
            // the live sets, so the retry only touches what actually remains.
            _runtime.AbortShutdown();
            _logger.LogWarning("CalloraVoipSdk runtime shutdown was aborted; the runtime stays resumable.");
            throw;
        }

        _runtime.CompleteShutdown();
    }

    /// <summary>
    /// Convenience registration flow: registers one line and waits for a terminal connect outcome.
    /// Existing <see cref="PhoneLineManager.Register"/> remains unchanged.
    /// </summary>
    [Obsolete("Use ConnectAsync(...) instead. RegisterAndWaitAsync(...) has been deprecated since v1.0 and is kept for backward compatibility; it may be removed in a future major release.", false)]
    public Task<ConnectResult> RegisterAndWaitAsync(
        SipAccount account,
        ConnectOptions? options = null,
        CancellationToken ct = default) =>
        ConnectAsync(account, options, ct);

    /// <summary>
    /// Convenience registration flow: registers one line and waits for a terminal connect outcome.
    /// Existing <see cref="PhoneLineManager.Register"/> remains unchanged.
    /// </summary>
    public async Task<ConnectResult> ConnectAsync(
        SipAccount account,
        ConnectOptions? options = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(account);
        options ??= ConnectOptions.Default;

        // Bind the per-line TLS override before registration so the line factory sees it when the line
        // is created (#183). Stored by account instance; a null override leaves the client-wide default.
        if (options.LineTls is { } lineTls)
            _lineTlsByAccount[account] = lineTls;

        try
        {
            var outcome = await _convenienceOrchestrator
                .RegisterAndWaitAsync(
                    account,
                    options.Timeout,
                    options.FailFastOnRegistrationFailed,
                    ct)
                .ConfigureAwait(false);

            return outcome.Status switch
            {
                LineConnectStatus.Registered when outcome.Line is not null =>
                    ConnectResult.Registered(outcome.Line),
                LineConnectStatus.Timeout =>
                    ConnectResult.Timeout(outcome.Line, outcome.FinalState),
                LineConnectStatus.Canceled =>
                    ConnectResult.Canceled(outcome.Line, outcome.FinalState),
                _ =>
                    ConnectResult.Failed(outcome.Line, outcome.Error, outcome.FinalState),
            };
        }
        finally
        {
            // The line factory (PhoneLineManager.Register) consumes the override synchronously while the
            // line is created, and the SipLineChannel then holds it for its lifetime. Drop the map entry
            // so the map is bounded to in-flight connects and never retains SipAccount / caller-owned
            // certificate references for the client's lifetime (#183 review H2).
            _lineTlsByAccount.TryRemove(account, out _);
        }
    }

    /// <inheritdoc />
    public Task SendMessageAsync(string targetUri, string body, string contentType = "text/plain", CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var line = Lines.All.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No registered line to send a SIP MESSAGE from. Register a line first, or use IPhoneLine.SendMessageAsync.");
        return line.SendMessageAsync(targetUri, body, contentType, ct);
    }

    /// <inheritdoc />
    public Task<Core.Domain.Publications.PublishResult> PublishAsync(
        string eventType, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var line = Lines.All.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No registered line to publish from. Register a line first, or use IPhoneLine.PublishAsync.");
        return line.PublishAsync(eventType, body, contentType, expiresSeconds, ct);
    }

    /// <inheritdoc />
    public Task<Core.Domain.Publications.PublishResult> RefreshPublicationAsync(
        string eventType, string etag, int expiresSeconds = 3600, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var line = Lines.All.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No registered line to publish from. Register a line first, or use IPhoneLine.RefreshPublicationAsync.");
        return line.RefreshPublicationAsync(eventType, etag, expiresSeconds, ct);
    }

    /// <inheritdoc />
    public Task<Core.Domain.Publications.PublishResult> ModifyPublicationAsync(
        string eventType, string etag, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var line = Lines.All.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No registered line to publish from. Register a line first, or use IPhoneLine.ModifyPublicationAsync.");
        return line.ModifyPublicationAsync(eventType, etag, body, contentType, expiresSeconds, ct);
    }

    /// <inheritdoc />
    public Task RemovePublicationAsync(string eventType, string etag, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var line = Lines.All.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No registered line to publish from. Register a line first, or use IPhoneLine.RemovePublicationAsync.");
        return line.RemovePublicationAsync(eventType, etag, ct);
    }

    /// <summary>
    /// Convenience outbound flow: dials a target and waits until the call reaches connected state.
    /// Existing <see cref="IPhoneLine.DialAsync"/> remains unchanged.
    /// </summary>
    public async Task<DialResult> DialAndWaitUntilConnectedAsync(
        IPhoneLine line,
        string targetUri,
        DialWaitOptions? options = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUri);
        options ??= DialWaitOptions.Default;

        var outcome = await _convenienceOrchestrator
            .DialAndWaitUntilConnectedAsync(
                line,
                targetUri,
                options.DialOptions,
                options.ConnectTimeout,
                options.HangupOnTimeout,
                options.HangupOnCancellation,
                ct)
            .ConfigureAwait(false);

        return outcome.Status switch
        {
            CallConnectStatus.Connected when outcome.Call is not null =>
                DialResult.Connected(outcome.Call),
            CallConnectStatus.Timeout =>
                DialResult.Timeout(outcome.Call, outcome.FinalState),
            CallConnectStatus.Canceled =>
                DialResult.Canceled(outcome.Call, outcome.FinalState),
            _ =>
                DialResult.Failed(outcome.Call, outcome.Error, outcome.FinalState),
        };
    }

    /// <summary>
    /// Convenience audio flow: attaches SDK default audio routing (receiver, sender, configured
    /// audio device) to the specified call and auto-detaches on call termination.
    /// If another convenience default-audio route is active on a different call, it is replaced.
    /// </summary>
    public Task AttachDefaultAudioAsync(ICall call, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _convenienceOrchestrator.AttachDefaultAudioAsync(call, ct);
    }

    /// <summary>
    /// Convenience audio flow: detaches the default audio routing from the specified call.
    /// </summary>
    public Task DetachDefaultAudioAsync(ICall call, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _convenienceOrchestrator.DetachDefaultAudioAsync(call, ct);
    }

    /// <summary>
    /// Convenience video flow: attaches SDK default video routing (receiver, sender, and the
    /// application-supplied <c>IVideoDevice</c> codec package) to the specified call and auto-detaches on
    /// call termination. If another convenience default-video route is active on a different call, it is
    /// replaced. The SDK is transport-only and ships no codec, so a video device must be registered via
    /// dependency injection; otherwise this fails closed.
    /// </summary>
    /// <exception cref="InvalidOperationException">No video codec device is registered.</exception>
    public Task AttachDefaultVideoAsync(ICall call, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _convenienceOrchestrator.AttachDefaultVideoAsync(call, ct);
    }

    /// <summary>
    /// Convenience video flow: detaches the default video routing from the specified call.
    /// </summary>
    public Task DetachDefaultVideoAsync(ICall call, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _convenienceOrchestrator.DetachDefaultVideoAsync(call, ct);
    }

    /// <summary>
    /// Lists runtime-selectable input devices for the configured SDK audio device.
    /// </summary>
    public IReadOnlyList<AudioDeviceDescriptor> GetAvailableInputAudioDevices()
    {
        return DeviceManager.GetAvailableInputDevices();
    }

    /// <summary>
    /// Lists runtime-selectable output devices for the configured SDK audio device.
    /// </summary>
    public IReadOnlyList<AudioDeviceDescriptor> GetAvailableOutputAudioDevices()
    {
        return DeviceManager.GetAvailableOutputDevices();
    }

    /// <summary>
    /// Returns the current runtime audio-device snapshot.
    /// </summary>
    public AudioDeviceRuntimeSnapshot GetAudioDeviceRuntimeSnapshot()
    {
        return DeviceManager.GetRuntimeSnapshot();
    }

    /// <summary>
    /// Switches the configured SDK input device at runtime.
    /// </summary>
    /// <param name="deviceId">
    /// Device id from <see cref="GetAvailableInputAudioDevices"/>.
    /// Use <c>-1</c>, <c>null</c>, or empty for the platform default input.
    /// </param>
    public void SwitchAudioInputDevice(string? deviceId)
    {
        DeviceManager.SwitchInputDevice(deviceId);
    }

    /// <summary>
    /// Switches the configured SDK output device at runtime.
    /// </summary>
    /// <param name="deviceId">
    /// Device id from <see cref="GetAvailableOutputAudioDevices"/>.
    /// Use <c>-1</c>, <c>null</c>, or empty for the platform default output.
    /// </param>
    public void SwitchAudioOutputDevice(string? deviceId)
    {
        DeviceManager.SwitchOutputDevice(deviceId);
    }

    /// <summary>
    /// Sets runtime input gain for the configured SDK audio device.
    /// </summary>
    /// <param name="volume">Linear gain in range 0..2 (0 = silence, 1 = neutral).</param>
    public void SetAudioInputVolume(float volume)
    {
        DeviceManager.SetInputVolume(volume);
    }

    /// <summary>
    /// Sets runtime output gain for the configured SDK audio device.
    /// </summary>
    /// <param name="volume">Linear gain in range 0..2 (0 = silence, 1 = neutral).</param>
    public void SetAudioOutputVolume(float volume)
    {
        DeviceManager.SetOutputVolume(volume);
    }

    /// <summary>
    /// Mutes or unmutes runtime microphone capture.
    /// </summary>
    public void SetAudioInputMuted(bool isMuted)
    {
        DeviceManager.SetInputMuted(isMuted);
    }

    /// <summary>
    /// Mutes or unmutes runtime speaker playback.
    /// </summary>
    public void SetAudioOutputMuted(bool isMuted)
    {
        DeviceManager.SetOutputMuted(isMuted);
    }

    /// <summary>
    /// Updates runtime capture/playback format for the configured SDK audio device.
    /// </summary>
    public void UpdateAudioFormat(AudioDeviceFormat format)
    {
        DeviceManager.UpdateFormat(format);
    }

    /// <summary>
    /// Registers a simplified asynchronous inbound call handler.
    /// This is additive convenience; <see cref="IncomingCall"/> remains fully available.
    /// </summary>
    public IDisposable OnIncomingCall(Func<ICall, Task> handler)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(handler);

        EventHandler<IncomingCallEventArgs> subscription = (_, args) =>
        {
            _ = InvokeIncomingHandlerAsync(args.Call, handler);
        };

        IncomingCall += subscription;
        return new IncomingCallSubscription(this, subscription);
    }

    /// <summary>
    /// Resolves a typed service instance from an optional service provider.
    /// </summary>
    private static T? ResolveService<T>(IServiceProvider? services)
        where T : class =>
        services?.GetService(typeof(T)) as T;

    private static bool IsTransportInitializationFailure(Exception ex) =>
        ex is SocketException or UnauthorizedAccessException;

    private static SipTransportProtocol MapTransport(SipTransport transport) => transport switch
    {
        SipTransport.Udp => SipTransportProtocol.Udp,
        SipTransport.Tcp => SipTransportProtocol.Tcp,
        SipTransport.Tls => SipTransportProtocol.Tls,
        SipTransport.Ws => SipTransportProtocol.Ws,
        SipTransport.Wss => SipTransportProtocol.Wss,
        _ => SipTransportProtocol.Udp
    };

    private IAudioDeviceRuntimeControl GetAudioDeviceRuntimeControl()
    {
        if (_audioDeviceRuntimeControl is not null)
            return _audioDeviceRuntimeControl;

        throw new NotSupportedException(
            $"Configured audio device '{_audioDevice.GetType().Name}' does not support runtime controls.");
    }

    /// <summary>
    /// Disposes lines, transport, and owned audio resources.
    /// </summary>
    /// <remarks>
    /// Double-dispose is safe (the first caller wins via an interlocked guard), but disposal is <b>not</b>
    /// synchronized against in-flight operations: calling <see cref="Dispose"/> while a <c>ConnectAsync</c>,
    /// <c>DialAndWaitUntilConnectedAsync</c> or a call/media operation is still running tears the underlying
    /// orchestrators and transport out from under it and may fault that operation. Let outstanding
    /// operations complete (or cancel them via their token) before disposing the client.
    /// </remarks>
    public void Dispose()
    {
        // Claim disposal atomically so two concurrent Dispose() callers cannot both run the teardown
        // (double-dispose of orchestrators/transport/audio); mirrors the runtime-state guard (HARD-C4).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Close module registration before anything is torn down (#166 P3-13), so a module cannot attach itself
        // to a client whose transport, lines and media are going away. Null-tolerant for the failed-constructor
        // path below; already registered modules stay resolvable during the teardown.
        (Modules as ModuleRegistry)?.MarkOwnerDisposed();

        // Null-tolerant: Dispose() also runs from a failed constructor (see the ctor's catch), where an
        // early throw can leave later fields unassigned. DisposeSafely no-ops on null, so partial init
        // still releases everything already built. In the normal path all fields are set, so behaviour is
        // byte-identical to disposing each member directly, and the _owns* guards are preserved.
        DisposeSafely(_convenienceOrchestrator);
        DisposeSafely(_mediaOrchestrator);
        // The media manager owns the running recording/playback sessions (#165 P2-8) and releases them
        // asynchronously; Dispose is synchronous, so the drain is fire-and-forget with its fault observed,
        // exactly as the orchestrator releases its media sessions here.
        DisposeSafely(Media);
        DisposeSafely(Lines);

        if (_ownsCallSignalingService)
            DisposeSafely(_callSignalingService);

        if (_ownsRegistrationService)
            DisposeSafely(_registrationService);

        DisposeSafely(_transportRuntime);

        if (_ownsAudioDevice)
            DisposeSafely(_audioDevice);
    }

    /// <summary>
    /// Disposes <paramref name="candidate"/> if it is a non-null <see cref="IDisposable"/>; otherwise a no-op.
    /// Tolerates the partially initialised state left by a constructor that threw before every field was set.
    /// Best-effort (#166 P1-2): a component whose <c>Dispose</c> throws must not abort the rest of the teardown
    /// chain (which would leak sockets, registrations or audio), and — when <see cref="Dispose"/> runs from the
    /// constructor's catch — must not replace the original initialisation error with the disposal fault. The
    /// disposal fault is logged and swallowed so cleanup continues and the caught error propagates unchanged.
    /// </summary>
    private void DisposeSafely(object? candidate)
    {
        if (candidate is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disposing {Component} during VoipClient teardown failed.", candidate.GetType().Name);
            }

            return;
        }

        // An async-only component (the media manager, #165 P2-8): Dispose is synchronous, so the teardown is
        // started here and its fault observed on a continuation rather than blocking the caller or being
        // dropped. Same shape as CallMediaOrchestrator.Dispose releasing its media sessions (#17.12).
        if (candidate is not IAsyncDisposable asyncDisposable)
            return;

        var componentName = candidate.GetType().Name;
        _ = asyncDisposable.DisposeAsync().AsTask().ContinueWith(
            task => _logger.LogWarning(
                task.Exception, "Disposing {Component} during VoipClient teardown failed.", componentName),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task InvokeIncomingHandlerAsync(ICall call, Func<ICall, Task> handler)
    {
        try
        {
            await handler(call).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Incoming call handler was canceled for call {CallId}.", call.CallId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incoming call handler failed for call {CallId}.", call.CallId);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(VoipClient));
    }

}
