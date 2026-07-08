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
using CalloraVoipSdk.Core.Security;

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
    private IReadOnlyCollection<System.Net.IPAddress>? _trustedRegistrarAddresses;

    // NAT: public address learned from the registrar's Via received=/rport= (N2).
    // Single source of truth for the advertised Contact/Via when no manual override is set.
    private string? _learnedPublicHost;
    private int? _learnedPublicPort;

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
        IReadOnlyList<string>? preferredCodecNames = null)
    {
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

        // Reset binding state for a fresh registration session.
        _registrationCallId = null;
        _registrationNextCSeq = 1;

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
        CancellationTokenSource? registrationCts;
        lock (_sync)
        {
            registrationCts = _registrationCts;
            _registrationCts = null;
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

        _ = UnregisterAsyncSafe();
        _onState?.Invoke(LineState.Unregistered);
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
        return new SipCoreCallChannel(
            _callChannelLogger,
            _sdpNegotiator,
            _telemetry,
            resolvedPolicy.Policy,
            resolvedPolicy.Source,
            _iceAgent,
            _preferredCodecNames);
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
        var localIp = ResolveLocalIp(_account.SipServer, _account.EffectivePort);
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
            PreloadedRouteSet  = BuildRouteSet(options.OutboundProxy ?? _account.OutboundProxy)
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

        var channel = new SipCoreCallChannel(
            _callChannelLogger,
            _sdpNegotiator,
            _telemetry,
            _globalSrtpPolicy,
            policySource: "global",
            _iceAgent,
            _preferredCodecNames);
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
        var hadSuccessfulRegistration = false;
        var options = _account.Reregister;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // RFC 3261 §10.2.4: pass persisted Call-ID and CSeq for binding refresh.
                    var request = CreateRegistrationRequest(_registrationCallId, _registrationNextCSeq);
                    var result = await _registrationService.RegisterAsync(request, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        return;

                    // Persist Call-ID and next CSeq for the next refresh cycle.
                    _registrationCallId = result.CallId;
                    _registrationNextCSeq = result.NextCSeq;

                    // NAT: adopt the public address the registrar reflected. When it changes
                    // the learned state (fresh discovery or IP change), re-register once
                    // immediately so the Contact becomes routable; an unchanged observation
                    // falls through to the normal refresh — this cannot loop.
                    var (host, port, changed) = NatPublicContactState.ApplyObserved(
                        HasManualPublicOverride,
                        _learnedPublicHost,
                        _learnedPublicPort,
                        result.ObservedPublicHost,
                        result.ObservedPublicPort);
                    if (changed)
                    {
                        _learnedPublicHost = host;
                        _learnedPublicPort = port;
                        _logger.LogDebug(
                            "SIP registration for [{User}]: learned public contact {Host}:{Port} from registrar; re-registering.",
                            _account.Username, host, port?.ToString() ?? "(local)");
                        continue;
                    }

                    failureCount = 0;
                    hadSuccessfulRegistration = true;
                    _onState?.Invoke(LineState.Registered);

                    var refreshDelay = ComputeRefreshDelay(result.EffectiveExpiresSeconds);
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
    private async Task UnregisterAsyncSafe()
    {
        try
        {
            // Use a fresh Call-ID for the unregister — it removes the specific binding
            // and is not a refresh of the existing registration.
            await _registrationService.UnregisterAsync(CreateRegistrationRequest(), CancellationToken.None)
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
            _account.InboundNumbers);

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
            PublicHost = HasManualPublicOverride ? _account.PublicSipHost : _learnedPublicHost,
            PublicPort = HasManualPublicOverride ? _account.PublicSipPort : _learnedPublicPort
        };

    private bool HasManualPublicOverride => !string.IsNullOrWhiteSpace(_account.PublicSipHost);

    /// <summary>
    /// Maps domain SIP transport choice to infrastructure transport protocol.
    /// </summary>
    private static Transport.SipTransportProtocol MapTransport(SipTransport transport) => transport switch
    {
        SipTransport.Tcp => Transport.SipTransportProtocol.Tcp,
        SipTransport.Tls => Transport.SipTransportProtocol.Tls,
        SipTransport.Ws => Transport.SipTransportProtocol.Ws,
        SipTransport.Wss => Transport.SipTransportProtocol.Wss,
        _ => Transport.SipTransportProtocol.Udp
    };

    /// <summary>
    /// Computes next registration refresh delay based on effective expires value.
    /// </summary>
    private static TimeSpan ComputeRefreshDelay(int effectiveExpiresSeconds)
    {
        var baseline = effectiveExpiresSeconds > 0 ? effectiveExpiresSeconds : 300;
        var refreshSeconds = (int)Math.Round(baseline * 0.8, MidpointRounding.AwayFromZero);
        refreshSeconds = Math.Clamp(refreshSeconds, 5, Math.Max(5, baseline - 1));
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
