using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using CalloraVoipSdk.Core.Domain.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Bridges line-level domain operations to the SIP core signaling services.
/// Handles registration, inbound INVITE dispatch, and outbound session bootstrap.
/// </summary>
internal sealed class SipLineChannel : ILineChannel
{
    private readonly SipAccount _account;
    private readonly string _userAgent;
    private readonly ISipRegistrationService _registrationService;
    private readonly ISipCallSignalingService _callSignalingService;
    private readonly ISdpNegotiator _sdpNegotiator;
    private readonly ICallIceAgent? _iceAgent;
    private readonly SrtpPolicy _globalSrtpPolicy;
    private readonly ISipTelemetrySink _telemetry;
    private readonly IReadOnlyList<string>? _preferredCodecNames;
    private readonly SdpDtlsNegotiationOptions? _dtlsOptions;
    private readonly bool _offerDtlsSrtp;
    private readonly bool _requireSecureSignalingForSdes;
    private bool _warnedSdesInsecureSignaling;
    private readonly bool _enableVideo;
    private readonly IReadOnlyList<string>? _preferredVideoCodecNames;
    private readonly ILogger<SipLineChannel> _logger;
    private readonly ILogger<SipCoreCallChannel> _callChannelLogger;
    private readonly object _sync = new();

    private Action<LineState>? _onState;
    private Action<int>? _onReconnecting;
    private Action<ReregisterFailReason, int>? _onReconnectFailed;
    private Action<ICallChannel, string>? _onInbound;
    private CancellationTokenSource? _registrationCts;
    private int _disposed;

    // RFC 3261 §10.2.4: preserve Call-ID and CSeq across re-registrations within one session.
    private string? _registrationCallId;
    private int _registrationNextCSeq = 1;

    // Resolved once (background registration loop), read on the inbound-INVITE thread —
    // published via a volatile reference for safe cross-thread visibility.
    private volatile IReadOnlyCollection<System.Net.IPAddress>? _trustedRegistrarAddresses;

    // NAT: public address learned from the registrar's Via received=/rport= (N2). Written on
    // the registration loop, read on the inbound-INVITE thread; held as a single immutable
    // record behind a volatile reference so readers never see a torn host/port pair.
    private volatile LearnedPublicContact? _learnedPublicContact;

    /// <summary>
    /// Creates a SIP line channel and wires registration and inbound signaling handlers.
    /// </summary>
    internal SipLineChannel(
        SipAccount account,
        string userAgent,
        ISipRegistrationService registrationService,
        ISipCallSignalingService callSignalingService,
        ISdpNegotiator sdpNegotiator,
        ICallIceAgent? iceAgent,
        SrtpPolicy globalSrtpPolicy,
        ISipTelemetrySink? telemetry,
        ILoggerFactory loggerFactory,
        IReadOnlyList<string>? preferredCodecNames = null,
        SdpDtlsNegotiationOptions? dtlsOptions = null,
        bool offerDtlsSrtp = false,
        bool enableVideo = false,
        IReadOnlyList<string>? preferredVideoCodecNames = null,
        bool requireSecureSignalingForSdes = false)
    {
        _dtlsOptions = dtlsOptions;
        _offerDtlsSrtp = offerDtlsSrtp && dtlsOptions is not null;
        _requireSecureSignalingForSdes = requireSecureSignalingForSdes;
        _enableVideo = enableVideo;
        _preferredVideoCodecNames = preferredVideoCodecNames;
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "CalloraVoipSdk/1.0" : userAgent;
        _registrationService = registrationService ?? throw new ArgumentNullException(nameof(registrationService));
        _callSignalingService = callSignalingService ?? throw new ArgumentNullException(nameof(callSignalingService));
        _sdpNegotiator = sdpNegotiator ?? throw new ArgumentNullException(nameof(sdpNegotiator));
        _iceAgent = iceAgent;
        _globalSrtpPolicy = globalSrtpPolicy;
        _telemetry = telemetry ?? NullSipTelemetrySink.Instance;
        _preferredCodecNames = preferredCodecNames;

        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<SipLineChannel>();
        _callChannelLogger = loggerFactory.CreateLogger<SipCoreCallChannel>();

        _callSignalingService.IncomingInvite += HandleIncomingInvite;
    }

    /// <summary>
    /// Starts SIP registration and wires callbacks for state transitions and reconnect events.
    /// </summary>
    /// <param name="onStateChange">Invoked on every <see cref="LineState"/> transition.</param>
    /// <param name="onReconnecting">
    /// Invoked when a reconnect attempt begins; parameter is the one-based attempt number.
    /// </param>
    /// <param name="onReconnectFailed">
    /// Invoked when re-registration fails permanently.
    /// First parameter is the <see cref="ReregisterFailReason"/>; second is the total attempt count.
    /// </param>
    public void StartRegistration(
        Action<LineState> onStateChange,
        Action<int>? onReconnecting = null,
        Action<ReregisterFailReason, int>? onReconnectFailed = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(onStateChange);

        _onState = onStateChange;
        _onReconnecting = onReconnecting;
        _onReconnectFailed = onReconnectFailed;
        onStateChange(LineState.Registering);

        // Reset binding state for a fresh registration session (under _sync so a concurrent
        // StopRegistration snapshot never reads a torn/stale binding identity).
        lock (_sync)
        {
            _registrationCallId = null;
            _registrationNextCSeq = 1;
        }

        var nextCts = new CancellationTokenSource();
        CancellationTokenSource? previousCts;
        lock (_sync)
        {
            previousCts = _registrationCts;
            _registrationCts = nextCts;
        }

        if (previousCts is not null)
        {
            try
            {
                previousCts.Cancel();
                previousCts.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to dispose previous registration token source for {User}.", _account.Username);
            }
        }

        _ = RegisterAsync(nextCts.Token);
    }

    /// <summary>
    /// Stops SIP registration and transitions line state to unregistered.
    /// </summary>
    public void StopRegistration()
    {
        var (registrationCallId, registrationCSeq) = StopRegistrationLoop();
        _ = UnregisterAsyncSafe(registrationCallId, registrationCSeq);
        _onState?.Invoke(LineState.Unregistered);
    }

    /// <inheritdoc />
    public async Task StopRegistrationAsync(CancellationToken ct = default)
    {
        var (registrationCallId, registrationCSeq) = StopRegistrationLoop();
        // Await the de-register (REGISTER Expires:0) so IPhoneLine.UnregisterAsync completes only
        // after the binding-removal round-trip, not merely after the refresh loop is cancelled.
        await UnregisterAsyncSafe(registrationCallId, registrationCSeq, ct).ConfigureAwait(false);
        _onState?.Invoke(LineState.Unregistered);
    }

    // Cancels the refresh loop and snapshots the live binding identity for the unregister
    // (RFC 3261 §10.2.2 removal reuses the registration's Call-ID + next CSeq).
    private (string? CallId, int CSeq) StopRegistrationLoop()
    {
        CancellationTokenSource? registrationCts;
        string? registrationCallId;
        int registrationCSeq;
        lock (_sync)
        {
            registrationCts = _registrationCts;
            _registrationCts = null;
            registrationCallId = _registrationCallId;
            registrationCSeq = _registrationNextCSeq;
        }

        if (registrationCts is not null)
        {
            try
            {
                registrationCts.Cancel();
                registrationCts.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to stop registration token source for {User}.", _account.Username);
            }
        }

        return (registrationCallId, registrationCSeq);
    }

    /// <summary>
    /// Sets the callback invoked for inbound call notifications.
    /// </summary>
    public void SetInboundHandler(Action<ICallChannel, string> onInbound)
        => _onInbound = onInbound;

    /// <summary>
    /// Builds an outbound call channel that will attach to one SIP dialog session.
    /// </summary>
    public ICallChannel PrepareOutboundChannel(DialOptions options)
    {
        ThrowIfDisposed();
        _ = options ?? throw new ArgumentNullException(nameof(options));
        var resolvedPolicy = SrtpPolicyEvaluator.ResolveEffectivePolicy(_globalSrtpPolicy, options.UseSrtp);
        GuardSdesKeyExposure(resolvedPolicy.Policy);
        return new SipCoreCallChannel(
            _callChannelLogger,
            _sdpNegotiator,
            _telemetry,
            resolvedPolicy.Policy,
            resolvedPolicy.Source,
            _iceAgent,
            _preferredCodecNames,
            PublicMediaAddress,
            _dtlsOptions,
            _offerDtlsSrtp,
            _enableVideo,
            _preferredVideoCodecNames);
    }

    /// <summary>
    /// Starts dialing to the remote SIP target using the prepared core signaling channel.
    /// </summary>
    public async Task StartOutboundDialAsync(
        ICallChannel channel,
        string targetUri,
        DialOptions options,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        if (channel is not SipCoreCallChannel sipChannel)
            throw new ArgumentException("Channel must be a SIP core call channel.", nameof(channel));

        // Resolve the local IP toward the SIP server so the SDP offer
        // advertises the correct address for RTP (not the loopback or 0.0.0.0).
        // An explicit public media override (CGNAT / static 1:1 NAT) wins when configured.
        var localIp = PublicMediaAddress ?? ResolveLocalIp(_account.SipServer, _account.EffectivePort);
        var localMediaEndPoint = new IPEndPoint(localIp, sipChannel.LocalMediaPort);

        // Build the SDP offer with the actual local media endpoint.
        // SRTP policy is evaluated in the channel after offer/answer completion.
        var sdpOffer = await sipChannel
            .BuildOfferSdpAsync(localMediaEndPoint, hold: false, ct)
            .ConfigureAwait(false);

        var displayName = options.DisplayName ?? _account.DisplayName;
        var inviteRequest = new SipInviteRequest
        {
            LocalUsername    = _account.Username,
            LocalDomain      = _account.SipServer,
            LocalDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            AuthUsername     = _account.Username,
            AuthPassword     = _account.Password,
            RemoteUri        = targetUri,
            RemotePort       = _account.EffectivePort,
            Timeout          = options.RingTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : options.RingTimeout,
            UserAgent        = _userAgent,
            Transport        = MapTransport(_account.Transport),
            SessionDescription = sdpOffer,
            PreloadedRouteSet  = BuildRouteSet(options.OutboundProxy ?? _account.OutboundProxy),
            CustomHeaders      = options.CustomHeaders
        };

        var session = await _callSignalingService.InviteAsync(inviteRequest, ct).ConfigureAwait(false);
        sipChannel.AttachSession(session);
    }

    /// <summary>
    /// Resolves the local outbound IP toward the given host by probing a UDP socket.
    /// Falls back to loopback when resolution fails.
    /// </summary>
    private static IPAddress ResolveLocalIp(string remoteHost, int remotePort)
    {
        try
        {
            using var probe = new System.Net.Sockets.UdpClient();
            probe.Connect(remoteHost, remotePort);
            return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Address;
        }
        catch
        {
            return System.Net.IPAddress.Loopback;
        }
    }

    /// <summary>
    /// Builds a preloaded Route-set entry for an outbound proxy (RFC 3261 §16.12).
    /// Returns an empty list when no proxy is configured.
    /// </summary>
    private static IReadOnlyList<string> BuildRouteSet(string? outboundProxy)
    {
        if (string.IsNullOrWhiteSpace(outboundProxy)) return [];
        // Ensure loose-routing parameter is present (RFC 3261 §19.1.1).
        var uri = outboundProxy.TrimEnd();
        if (!uri.Contains(";lr", StringComparison.OrdinalIgnoreCase))
            uri += ";lr";
        return [$"<{uri}>"];
    }

    /// <summary>
    /// Handles inbound INVITE events from call signaling and dispatches to line domain callback.
    /// </summary>
    private void HandleIncomingInvite(object? sender, SipIncomingInviteEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (args?.Session is null)
            return;

        if (!IsSessionForThisLine(args.Session))
        {
            _logger.LogDebug(
                "Ignoring inbound SIP session {CallId} on line [{User}]: local URI '{LocalUri}' does not match account username.",
                args.Session.CallId,
                _account.Username,
                args.Session.LocalUri);
            return;
        }

        // NAT: advertise our public address in the in-dialog Contact (so the peer routes the
        // ACK to our 2xx) and in the media SDP. Manual override wins, else the learned
        // rport/received address.
        var learned = _learnedPublicContact;
        var publicHost = HasManualPublicOverride ? _account.PublicSipHost : learned?.Host;
        var publicPort = HasManualPublicOverride ? _account.PublicSipPort : learned?.Port;
        args.Session.SetAdvertisedPublicContact(publicHost, publicPort);

        var channel = new SipCoreCallChannel(
            _callChannelLogger,
            _sdpNegotiator,
            _telemetry,
            _globalSrtpPolicy,
            policySource: "global",
            _iceAgent,
            _preferredCodecNames,
            PublicMediaAddress,
            _dtlsOptions,
            _offerDtlsSrtp,
            _enableVideo,
            _preferredVideoCodecNames);
        channel.AttachSession(args.Session);

        try
        {
            _onInbound?.Invoke(channel, args.Session.RemoteUri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inbound call dispatch failed on line [{User}].", _account.Username);
            channel.Dispose();
        }
    }

    /// <summary>
    /// Executes the REGISTER loop with automatic re-registration and configurable backoff.
    /// After a successful registration, failures use <see cref="LineState.Reconnecting"/> and fire
    /// the reconnect callbacks.  Permanent failures (auth errors or max retries exceeded) transition
    /// the line to <see cref="LineState.Failed"/> and stop the loop.
    /// </summary>
    private async Task RegisterAsync(CancellationToken ct)
    {
        var failureCount = 0;
        var correctiveReregistrations = 0;
        var hadSuccessfulRegistration = false;
        var options = _account.Reregister;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // RFC 3261 §10.2.4: pass persisted Call-ID and CSeq for binding refresh.
                    // Snapshot/persist the binding identity under _sync so a concurrent
                    // StopRegistration reads a consistent Call-ID+CSeq for the unregister.
                    string? bindingCallId;
                    int bindingCSeq;
                    lock (_sync)
                    {
                        bindingCallId = _registrationCallId;
                        bindingCSeq = _registrationNextCSeq;
                    }
                    var request = CreateRegistrationRequest(bindingCallId, bindingCSeq);
                    var result = await _registrationService.RegisterAsync(request, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        return;

                    // Persist Call-ID and next CSeq for the next refresh cycle.
                    lock (_sync)
                    {
                        _registrationCallId = result.CallId;
                        _registrationNextCSeq = result.NextCSeq;
                    }

                    // NAT: adopt the public address the registrar reflected. When it changes
                    // the learned state (fresh discovery or IP change), re-register once
                    // immediately so the Contact becomes routable; an unchanged observation
                    // falls through to the normal refresh. A per-cycle cap
                    // (ReregisterOptions.MaxCorrectiveReregistrations) bounds a re-register
                    // storm on a pathological NAT that reflects a new port on every REGISTER.
                    var (host, port, changed) = NatPublicContactState.ApplyObserved(
                        HasManualPublicOverride,
                        _learnedPublicContact?.Host,
                        _learnedPublicContact?.Port,
                        result.ObservedPublicHost,
                        result.ObservedPublicPort);
                    // The NAT-corrective re-register applies only to connectionless transport (UDP).
                    // Over connection-oriented transports (TCP/TLS/WS) the persistent connection carries
                    // the routing (RFC 5626 SIP Outbound); rewriting the Contact to the registrar-observed
                    // SNAT address would break the very next re-register, because its ephemeral source
                    // port no longer matches the connection → registrar 403 (interop finding F010).
                    var natCorrectionApplies = _account.Transport is SipTransport.Udp;
                    if (changed && natCorrectionApplies)
                    {
                        _learnedPublicContact = host is null ? null : new LearnedPublicContact(host, port);
                        if (correctiveReregistrations < options.MaxCorrectiveReregistrations)
                        {
                            correctiveReregistrations++;
                            _logger.LogDebug(
                                "SIP registration for [{User}]: learned public contact {Host}:{Port} from registrar; re-registering ({Attempt}/{Max}).",
                                _account.Username, host, port?.ToString() ?? "(local)",
                                correctiveReregistrations, options.MaxCorrectiveReregistrations);
                            continue;
                        }

                        _logger.LogWarning(
                            "SIP registration for [{User}]: NAT-reflected public contact keeps changing (now {Host}:{Port}); settling after {Max} corrective re-registrations to avoid churn.",
                            _account.Username, host, port?.ToString() ?? "(local)", options.MaxCorrectiveReregistrations);
                        // Fall through: adopt the latest address, register as-is, stop correcting.
                    }

                    correctiveReregistrations = 0;
                    failureCount = 0;
                    hadSuccessfulRegistration = true;
                    // Resolve trusted registrar peers here (background loop), so the inbound
                    // INVITE path never blocks on DNS; the result is cached and volatile.
                    _ = ResolveTrustedRegistrarAddresses();
                    _onState?.Invoke(LineState.Registered);

                    var refreshDelay = ComputeRefreshDelay(result.EffectiveExpiresSeconds, options);
                    _logger.LogDebug(
                        "SIP registration for [{User}] will refresh in {Delay}.",
                        _account.Username,
                        refreshDelay);
                    await Task.Delay(refreshDelay, ct).ConfigureAwait(false);
                    _onState?.Invoke(LineState.Registering);
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogDebug(ex, "Registration was canceled for line [{User}].", _account.Username);
                    return;
                }
                catch (Exception ex)
                {
                    failureCount++;

                    // Permanent failure: auth rejection — retrying with same credentials is pointless.
                    if (IsAuthFailure(ex))
                    {
                        _logger.LogWarning(
                            ex,
                            "SIP registration permanently rejected for [{User}]: authentication failure (attempt {Count}).",
                            _account.Username, failureCount);
                        _onState?.Invoke(LineState.Failed);
                        _onReconnectFailed?.Invoke(ReregisterFailReason.AuthenticationFailed, failureCount);
                        return;
                    }

                    // Auto-reregister disabled — report failure and stop.
                    if (!options.AutoReregister)
                    {
                        _logger.LogWarning(
                            ex,
                            "Registration failed for [{User}]; auto-reregister is disabled.",
                            _account.Username);
                        _onState?.Invoke(LineState.RegistrationFailed);
                        return;
                    }

                    // Permanent failure: max retries exceeded.
                    if (options.MaxRetries > 0 && failureCount > options.MaxRetries)
                    {
                        _logger.LogWarning(
                            ex,
                            "Registration permanently failed for [{User}] after {Count} attempts (max {Max}).",
                            _account.Username, failureCount, options.MaxRetries);
                        _onState?.Invoke(LineState.Failed);
                        _onReconnectFailed?.Invoke(ReregisterFailReason.MaxRetriesExceeded, failureCount);
                        return;
                    }

                    // Transient failure — use Reconnecting state after the first successful registration,
                    // RegistrationFailed for the initial registration phase.
                    _logger.LogWarning(
                        ex,
                        "Registration failed for [{User}] (attempt {Count}){Reconnect}.",
                        _account.Username,
                        failureCount,
                        hadSuccessfulRegistration ? "; reconnecting" : string.Empty);

                    if (hadSuccessfulRegistration)
                    {
                        _onState?.Invoke(LineState.Reconnecting);
                        _onReconnecting?.Invoke(failureCount);
                    }
                    else
                    {
                        _onState?.Invoke(LineState.RegistrationFailed);
                    }

                    var recoveryDelay = ComputeBackoffDelay(failureCount, options);
                    _logger.LogDebug(
                        "SIP registration recovery for [{User}] scheduled in {Delay}.",
                        _account.Username,
                        recoveryDelay);
                    await Task.Delay(recoveryDelay, ct).ConfigureAwait(false);
                    _onState?.Invoke(LineState.Registering);
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Registration loop canceled for line [{User}].", _account.Username);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the exception represents a permanent SIP authentication
    /// failure (401 Unauthorized or 403 Forbidden).
    /// </summary>
    private static bool IsAuthFailure(Exception ex)
        => ex is SipRegistrationFailedException { StatusCode: 401 or 403 };

    /// <summary>
    /// Performs unREGISTER as best-effort cleanup and isolates failures.
    /// </summary>
    private async Task UnregisterAsyncSafe(string? registrationCallId, int registrationCSeq, CancellationToken ct = default)
    {
        try
        {
            // Remove the binding by re-using the registration's own Call-ID and next CSeq
            // (RFC 3261 §10.2.2; Expires:0 is applied by UnregisterAsync). A fresh Call-ID + CSeq 1
            // is not recognised by registrars as removing the existing binding, which then lingers
            // until expiry — leaving a second, dead binding that forks inbound INVITEs into the void.
            await _registrationService
                .UnregisterAsync(CreateRegistrationRequest(registrationCallId, registrationCSeq), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unregister failed for line [{User}] during stop/dispose.", _account.Username);
        }
    }

    /// <summary>
    /// Returns true when an inbound session local URI targets this line account.
    /// </summary>
    private bool IsSessionForThisLine(ISipCallSession session) =>
        TrunkInboundMatcher.IsForThisLine(
            session.LocalUri,
            _account.Username,
            _account.SipServer,
            session.RemoteSignalingEndPoint?.Address,
            ResolveTrustedRegistrarAddresses(),
            _account.InboundNumbers,
            _account.AcceptTrunkInbound);

    /// <summary>
    /// Resolves and caches the registrar/outbound-proxy addresses this line trusts for
    /// inbound peer matching. Best-effort DNS; an unresolvable host contributes nothing.
    /// </summary>
    private IReadOnlyCollection<System.Net.IPAddress> ResolveTrustedRegistrarAddresses()
    {
        if (_trustedRegistrarAddresses is not null)
            return _trustedRegistrarAddresses;

        var addresses = new HashSet<System.Net.IPAddress>();
        foreach (var host in new[] { _account.SipServer, _account.OutboundProxy })
        {
            if (string.IsNullOrWhiteSpace(host))
                continue;
            var bareHost = SipProtocol.TryParseSipUri(host, out _, out var parsedHost, out _)
                ? parsedHost
                : host;
            try
            {
                foreach (var address in System.Net.Dns.GetHostAddresses(bareHost))
                    addresses.Add(address);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve trusted registrar host '{Host}' for inbound matching.", bareHost);
            }
        }

        _trustedRegistrarAddresses = addresses;
        return _trustedRegistrarAddresses;
    }

    /// <summary>
    /// Builds one registration request from line account configuration.
    /// Passes persisted Call-ID and CSeq when refreshing an existing binding (RFC 3261 §10.2.4).
    /// </summary>
    private SipRegistrationRequest CreateRegistrationRequest(
        string? existingCallId = null,
        int startCSeq = 1) =>
        new()
        {
            Username = _account.Username,
            Password = _account.Password,
            Domain = _account.SipServer,
            Port = _account.EffectivePort,
            DisplayName = string.IsNullOrWhiteSpace(_account.DisplayName) ? null : _account.DisplayName,
            ExpiresSeconds = Math.Max(1, _account.RegistrationExpiry),
            Timeout = TimeSpan.FromSeconds(10),
            UserAgent = _userAgent,
            Transport = MapTransport(_account.Transport),
            ExistingCallId = existingCallId,
            StartCSeq = startCSeq,
            // Manual override (N1) wins; otherwise the address learned from the
            // registrar's received=/rport= (N2); otherwise the local address.
            PublicHost = HasManualPublicOverride ? _account.PublicSipHost : _learnedPublicContact?.Host,
            PublicPort = HasManualPublicOverride ? _account.PublicSipPort : _learnedPublicContact?.Port
        };

    private bool HasManualPublicOverride => !string.IsNullOrWhiteSpace(_account.PublicSipHost);

    /// <summary>
    /// Configured public media IP override (<see cref="SipAccount.PublicMediaHost"/>) parsed to an
    /// <see cref="IPAddress"/>, or <see langword="null"/> when unset or not an IP literal. When set,
    /// it forces the SDP media connection address for calls on this line.
    /// </summary>
    private IPAddress? PublicMediaAddress =>
        !string.IsNullOrWhiteSpace(_account.PublicMediaHost)
            && IPAddress.TryParse(_account.PublicMediaHost, out var ip)
            ? ip
            : null;

    /// <summary>
    /// Maps domain SIP transport choice to infrastructure transport protocol.
    /// </summary>
    private static bool IsSecureSignaling(SipTransport transport) =>
        transport is SipTransport.Tls or SipTransport.Wss;

    // Surfaces the RFC 4568 §7 caveat once per line: an SDES a=crypto key sent over a signaling
    // transport without confidentiality (UDP/TCP/WS) travels in cleartext, so the offered SRTP gives
    // no real confidentiality against a passive eavesdropper on the signaling path. TLS/SIPS signaling
    // or DTLS-SRTP (keys never in the SDP) avoids this.
    private void GuardSdesKeyExposure(SrtpPolicy policy)
    {
        if (!SrtpPolicyEvaluator.ExposesSdesKeyOverInsecureSignaling(
                policy, _offerDtlsSrtp, IsSecureSignaling(_account.Transport)))
            return;

        // Opt-in hard enforcement: refuse rather than key SDES over insecure signaling (fail-closed,
        // analogous to SrtpPolicy.Required). The caller placed an outbound call whose SDES a=crypto key
        // would travel in cleartext SDP (RFC 4568 §7); with RequireSecureSignalingForSdes set, don't.
        if (_requireSecureSignalingForSdes)
            throw new InvalidOperationException(
                $"SRTP policy '{policy}' would key SDES over an insecure signaling transport ({_account.Transport}), " +
                "exposing the master key in cleartext SDP (RFC 4568 §7). RequireSecureSignalingForSdes is set, so the " +
                $"outbound call is refused. Use TLS/SIPS signaling or enable DTLS-SRTP. [{SrtpDecisionReasonCodes.SdesKeyOverInsecureSignaling}]");

        if (_warnedSdesInsecureSignaling)
            return;

        _warnedSdesInsecureSignaling = true;
        _logger.LogWarning(
            "SRTP policy '{Policy}' offers SDES keying over an insecure signaling transport ({Transport}): " +
            "the SRTP master key is carried in cleartext SDP (RFC 4568 §7), so media confidentiality is not " +
            "assured against a passive eavesdropper on the signaling path. Use TLS/SIPS signaling or enable " +
            "DTLS-SRTP for real media confidentiality. [{ReasonCode}]",
            policy, _account.Transport, SrtpDecisionReasonCodes.SdesKeyOverInsecureSignaling);
    }

    private static Transport.SipTransportProtocol MapTransport(SipTransport transport) => transport switch
    {
        SipTransport.Tcp => Transport.SipTransportProtocol.Tcp,
        SipTransport.Tls => Transport.SipTransportProtocol.Tls,
        SipTransport.Ws => Transport.SipTransportProtocol.Ws,
        SipTransport.Wss => Transport.SipTransportProtocol.Wss,
        _ => Transport.SipTransportProtocol.Udp
    };

    /// <summary>
    /// Computes the next registration refresh delay from the granted lifetime, using the
    /// account's <see cref="ReregisterOptions.RefreshRatio"/> and
    /// <see cref="ReregisterOptions.MinRefreshInterval"/>. Refreshes at ratio × lifetime,
    /// never later than the binding lives (<c>baseline - 1</c>) and — as a secondary guard
    /// against churn when a registrar reports an implausibly short lifetime — not sooner than
    /// the configured minimum, unless the binding itself is shorter than that.
    /// </summary>
    internal static TimeSpan ComputeRefreshDelay(int effectiveExpiresSeconds, ReregisterOptions options)
    {
        var baseline = effectiveExpiresSeconds > 0 ? effectiveExpiresSeconds : 300;
        var ratio = options.RefreshRatio is > 0 and < 1 ? options.RefreshRatio : 0.8;
        var refreshSeconds = (int)Math.Round(baseline * ratio, MidpointRounding.AwayFromZero);
        var upperBound = Math.Max(1, baseline - 1);
        var minRefresh = Math.Max(1, (int)Math.Round(options.MinRefreshInterval.TotalSeconds));
        var floor = Math.Min(minRefresh, upperBound);
        refreshSeconds = Math.Clamp(refreshSeconds, floor, upperBound);
        return TimeSpan.FromSeconds(refreshSeconds);
    }

    /// <summary>
    /// Computes exponential backoff delay for registration recovery using configured bounds.
    /// </summary>
    private static TimeSpan ComputeBackoffDelay(int failureCount, ReregisterOptions options)
    {
        var initial = options.InitialRetryDelay.TotalSeconds;
        var max = options.MaxRetryDelay.TotalSeconds;

        // Cap the exponent to prevent overflow; 10 already gives 512× initial delay.
        var exponent = Math.Clamp(failureCount - 1, 0, 10);
        var seconds = initial * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, initial, max));
    }

    /// <summary>
    /// Unsubscribes signaling handlers and stops registration resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _callSignalingService.IncomingInvite -= HandleIncomingInvite;
        StopRegistration();
    }

    /// <summary>
    /// Throws if this line channel was already disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipLineChannel));
    }
}
