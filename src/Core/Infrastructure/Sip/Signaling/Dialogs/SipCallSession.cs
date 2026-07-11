using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
/// <summary>
/// Concrete SIP dialog session implementing INVITE dialog state machine actions.
/// </summary>
internal sealed class SipCallSession : ISipCallSession, IDisposable
{
    private static readonly TimeSpan ReliableProvisionalT1 = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReliableProvisionalT2 = TimeSpan.FromSeconds(4);
    internal readonly ISipTransportRuntime _transport;
    internal readonly ISipDigestAuthenticator _digestAuthenticator;
    internal readonly ISipServerTransactionEngine _serverTransactions;
    private readonly ISipIdentityTrustPolicy _identityTrustPolicy;
    internal readonly ILogger _logger;
    internal readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SipCallSessionHeaderService _headerService;
    private readonly SipCallSessionTransactionService _transactionService;
    private readonly SipCallSessionInboundService _inboundService;
    private readonly SipCallSessionContextAdapter _context;
    private readonly SipReliableProvisionalManager _reliableProvisionalManager;
    private readonly SipSessionTimerManager _sessionTimerManager;
    internal readonly SipDialogManager _dialogManager = new();
    private readonly bool _isInbound;
    internal readonly string _localDisplayName;
    internal readonly string? _preferredIdentityUri;
    internal readonly string? _privacyHeader;
    internal readonly string? _requireHeader;
    internal readonly string? _proxyRequireHeader;
    internal readonly string? _referredBy;
    internal readonly string _authUsername;
    internal readonly string? _authPassword;
    internal readonly string _userAgent;
    internal readonly string _initialRequestUri;
    internal readonly IReadOnlyList<string> _initialRouteSet;
    internal readonly SipTransportProtocol _signalingTransport;
    internal readonly TimeSpan _timeout;
    internal readonly SipRequest? _initialInvite;
    internal IPEndPoint _remoteEndPoint;
    internal string? _advertisedPublicHost;
    internal int? _advertisedPublicPort;
    internal string? _localTag;
    internal string? _remoteTag;
    private int _localCSeq;
    private int _lastRemoteCSeq;
    private bool _hasRemoteCSeq;
    internal int _activeInviteCSeq;
    internal string? _activeInviteBranch;
    private string? _remoteAssertedIdentity;
    private string? _remoteSdp;
    private string? _localSdp;
    internal readonly SipSessionSdpProvider _sdpProvider;
    private SipDialogState _state;
    private SipDialogTerminationReason? _lastTerminationReason;
    internal int _disposed;
    /// <summary>
    /// Creates outbound SIP call session.
    /// </summary>
    public static SipCallSession CreateOutbound(
        SipCallSessionConfiguration configuration,
        SipCallSessionDependencies dependencies) =>
        new(
            configuration,
            dependencies,
            SipCallSessionInitialization.CreateOutbound());
    /// <summary>
    /// Creates inbound SIP call session from inbound INVITE.
    /// </summary>
    public static SipCallSession CreateInbound(
        SipCallSessionConfiguration configuration,
        SipInboundSessionContext inboundContext,
        SipCallSessionDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(inboundContext);
        var remoteTag = SipProtocol.ExtractTag(inboundContext.InitialInvite.Header("From"));
        return new SipCallSession(
            configuration,
            dependencies,
            SipCallSessionInitialization.CreateInbound(
                inboundContext.InitialInvite,
                inboundContext.LocalTag,
                remoteTag));
    }
    /// <summary>
    /// Creates a SIP dialog session.
    /// </summary>
    private SipCallSession(
        SipCallSessionConfiguration configuration,
        SipCallSessionDependencies dependencies,
        SipCallSessionInitialization initialization)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(initialization);
        _isInbound = initialization.IsInbound;
        CallId = configuration.CallId;
        LocalUri = configuration.LocalUri;
        RemoteUri = configuration.RemoteUri;
        _localDisplayName = configuration.LocalDisplayName ?? string.Empty;
        _preferredIdentityUri = configuration.PreferredIdentityUri;
        _privacyHeader = configuration.PrivacyHeader;
        _requireHeader = configuration.RequireHeader;
        _proxyRequireHeader = configuration.ProxyRequireHeader;
        _referredBy = configuration.ReferredBy;
        _authUsername = configuration.AuthUsername;
        _authPassword = configuration.AuthPassword;
        _userAgent = configuration.UserAgent;
        _initialRequestUri = string.IsNullOrWhiteSpace(configuration.InitialRequestUri)
            ? configuration.RemoteUri
            : configuration.InitialRequestUri!;
        _initialRouteSet = configuration.InitialRouteSet;
        _signalingTransport = configuration.SignalingTransport;
        _timeout = configuration.Timeout;
        _remoteEndPoint = configuration.RemoteEndPoint;
        _initialInvite = initialization.InitialInvite;
        _localTag = initialization.LocalTag;
        _remoteTag = initialization.RemoteTag;
        _state = initialization.InitialState;
        _transport = dependencies.Transport;
        _digestAuthenticator = dependencies.DigestAuthenticator;
        _serverTransactions = dependencies.ServerTransactions;
        _identityTrustPolicy = dependencies.IdentityTrustPolicy;
        _logger = dependencies.Logger;
        _sdpProvider = dependencies.SdpProvider;
        _context = new SipCallSessionContextAdapter(this);
        _headerService = new SipCallSessionHeaderService(_context);
        _transactionService = new SipCallSessionTransactionService(_context, _headerService);
        _inboundService = new SipCallSessionInboundService(_context, _headerService);
        _reliableProvisionalManager = new SipReliableProvisionalManager(_logger);
        _sessionTimerManager = new SipSessionTimerManager(
            _logger,
            SendSessionRefreshAsync,
            HandleSessionTimerExpiredAsync);
        if (_initialInvite is not null)
        {
            // For inbound sessions, the INVITE body is the remote SDP offer.
            if (!string.IsNullOrWhiteSpace(_initialInvite.Body))
                _remoteSdp = _initialInvite.Body;
            _dialogManager.ApplyInboundRequest(_initialInvite, RemoteUri);
            ApplyRemoteAssertedIdentity(
                _initialInvite.Header("P-Asserted-Identity"),
                configuration.RemoteEndPoint);
            var initialRemoteCSeq = SipProtocol.ExtractCSeqNumber(_initialInvite.Header("CSeq"));
            if (initialRemoteCSeq > 0)
            {
                _lastRemoteCSeq = initialRemoteCSeq;
                _hasRemoteCSeq = true;
            }
        }
    }
    /// <inheritdoc />
    public string CallId { get; }
    /// <inheritdoc />
    public string LocalUri { get; }
    /// <inheritdoc />
    public string RemoteUri { get; }
    /// <inheritdoc />
    public string? LocalTag { get { lock (_sync) return _localTag; } }
    /// <inheritdoc />
    public string? RemoteTag { get { lock (_sync) return _remoteTag; } }
    /// <inheritdoc />
    public bool IsInbound => _isInbound;
    /// <inheritdoc />
    public string? RemoteAssertedIdentity
    {
        get
        {
            lock (_sync) return _remoteAssertedIdentity;
        }
    }
    /// <inheritdoc />
    public SipDialogState State
    {
        get
        {
            lock (_sync) return _state;
        }
    }
    /// <inheritdoc />
    public string? RemoteSdp
    {
        get { lock (_sync) return _remoteSdp; }
    }
    /// <inheritdoc />
    public string? LocalSdp
    {
        get { lock (_sync) return _localSdp; }
    }
    /// <inheritdoc />
    public System.Net.IPEndPoint LocalSignalingEndPoint =>
        _transport.GetLocalEndPoint(_signalingTransport);
    /// <inheritdoc />
    public System.Net.IPEndPoint? RemoteSignalingEndPoint => _remoteEndPoint;
    /// <inheritdoc />
    public void SetAdvertisedPublicContact(string? host, int? port)
    {
        _advertisedPublicHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        _advertisedPublicPort = port is > 0 ? port : null;
    }
    /// <inheritdoc />
    public SipDialogTerminationReason? LastTerminationReason
    {
        get
        {
            lock (_sync) return _lastTerminationReason;
        }
    }
    /// <inheritdoc />
    public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged;
    /// <inheritdoc />
    public event EventHandler<bool>? RemoteHoldChanged;
    /// <inheritdoc />
    public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived;
    /// <inheritdoc />
    public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested;
    /// <inheritdoc />
    public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested;
    /// <inheritdoc />
    public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived;
    /// <summary>
    /// Starts outbound INVITE transaction and waits for call establishment.
    /// </summary>
    internal async Task StartOutboundInviteAsync(
        string? sessionDescription,
        string localTag,
        CancellationToken ct)
    {
        if (_isInbound) throw new InvalidOperationException("Inbound sessions cannot start outbound INVITE.");
        ThrowIfDisposed();
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        string body;
        try
        {
            if (State != SipDialogState.Idle)
                throw new InvalidOperationException($"Dialog must be Idle, current state is {State}.");
            lock (_sync) _localTag = localTag;
            TransitionTo(SipDialogState.Inviting);
            var localEndPoint = _transport.GetLocalEndPoint(_signalingTransport);
            body = sessionDescription ?? _sdpProvider.BuildOffer(localEndPoint, false);
        }
        finally
        {
            // RFC 3261 §9.1: CANCEL must be sendable while INVITE is pending.
            // Release the gate before the transaction so that a concurrent HangupAsync
            // can acquire it to send CANCEL without deadlocking.
            ReleaseOperationGateSafe();
        }
        await _transactionService.SendInviteTransactionAsync(
                body,
                allowRingingTransition: true,
                successState: SipDialogState.Established,
                ct)
            .ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task AnswerAsync(
        string? sessionDescription = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_isInbound)
            throw new InvalidOperationException("Only inbound sessions can be answered.");
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SipDialogState.Ringing)
                throw new InvalidOperationException($"Dialog must be Ringing, current state is {State}.");
            if (_initialInvite is null)
                throw new InvalidOperationException("Inbound INVITE context is missing.");
            if (string.IsNullOrWhiteSpace(_localTag))
                throw new InvalidOperationException("Local tag is missing.");
            if (!SipRequireOptionPolicy.TryValidateInviteRequireHeader(
                    _initialInvite.Header("Require"),
                    out var unsupportedHeaderValue))
            {
                var unsupportedHeaders = _headerService.CreateResponseHeadersFromRequest(
                    _initialInvite,
                    _localTag,
                    includeContentType: false);
                unsupportedHeaders["Unsupported"] = unsupportedHeaderValue;
                await _serverTransactions.SendResponseAsync(
                        _initialInvite,
                        _remoteEndPoint,
                        _signalingTransport,
                        statusCode: 420,
                        reasonPhrase: "Bad Extension",
                        unsupportedHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
                TransitionTo(SipDialogState.Terminated);
                return;
            }
            if (SipCallSessionUtilities.ShouldUseReliableProvisional(_initialInvite))
            {
                var prackAcknowledged = await SendReliableProvisionalAndWaitForPrackAsync(
                        _initialInvite,
                        _localTag,
                        ct)
                    .ConfigureAwait(false);
                if (!prackAcknowledged)
                {
                    TransitionTo(SipDialogState.Terminated);
                    return;
                }
            }
            if (!SipSessionTimerPolicy.TryValidateInboundRequest(
                    _initialInvite,
                    out var timerRejectionCode,
                    out var timerRejectionReasonPhrase,
                    out var normalizedSessionExpires))
            {
                var timerRejectHeaders = _headerService.CreateResponseHeadersFromRequest(_initialInvite, _localTag, includeContentType: false);
                if (timerRejectionCode == 422)
                    SipSessionTimerPolicy.ApplyTooSmallResponseHeaders(timerRejectHeaders);
                await _serverTransactions.SendResponseAsync(
                        _initialInvite,
                        _remoteEndPoint,
                        _signalingTransport,
                        statusCode: timerRejectionCode,
                        reasonPhrase: timerRejectionReasonPhrase,
                        timerRejectHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
                TransitionTo(SipDialogState.Terminated);
                return;
            }
            var body = sessionDescription;
            var headers = _headerService.CreateResponseHeadersFromRequest(_initialInvite, _localTag, includeContentType: !string.IsNullOrWhiteSpace(body));
            SipSessionTimerPolicy.ApplyResponseHeaders(headers, normalizedSessionExpires);
            await _serverTransactions.SendResponseAsync(
                    _initialInvite,
                    _remoteEndPoint,
                    _signalingTransport,
                    statusCode: 200,
                    reasonPhrase: "OK",
                    headers,
                    body,
                    ct)
                .ConfigureAwait(false);
            ApplySessionTimerNegotiation(
                headers.TryGetValue("Session-Expires", out var sessionExpires) ? sessionExpires : null,
                localIsRequester: false);
            TransitionTo(SipDialogState.Established);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <inheritdoc />
    public async Task RejectAsync(
        int statusCode = 486,
        string? reasonPhrase = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (statusCode < 400 || statusCode > 699)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Rejection status code must be 4xx, 5xx, or 6xx.");
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isInbound || State != SipDialogState.Ringing)
                throw new InvalidOperationException(
                    $"RejectAsync is only valid for inbound dialogs in Ringing state; current state is {State}.");
            if (_initialInvite is null || string.IsNullOrWhiteSpace(_localTag))
                throw new InvalidOperationException("Inbound INVITE context is missing.");
            var phrase = string.IsNullOrWhiteSpace(reasonPhrase)
                ? SipCallSessionUtilities.ResolveDefaultReasonPhrase(statusCode)
                : reasonPhrase;
            var rejectHeaders = _headerService.CreateResponseHeadersFromRequest(
                _initialInvite, _localTag, includeContentType: false);
            await _serverTransactions.SendResponseAsync(
                    _initialInvite,
                    _remoteEndPoint,
                    _signalingTransport,
                    statusCode,
                    phrase,
                    rejectHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            TransitionTo(
                SipDialogState.Terminated,
                SipReasonHeader.CreateSipStatusReason(statusCode, phrase));
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <inheritdoc />
    public async Task HangupAsync(
        CancellationToken ct = default,
        SipDialogTerminationReason? reason = null)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == SipDialogState.Terminated) return;
            if (_isInbound && State == SipDialogState.Ringing)
            {
                if (_initialInvite is not null && !string.IsNullOrWhiteSpace(_localTag))
                {
                    var rejectHeaders = _headerService.CreateResponseHeadersFromRequest(
                        _initialInvite,
                        _localTag,
                        includeContentType: false);
                    if (reason is not null)
                        rejectHeaders["Reason"] = SipReasonHeader.Format(reason);
                    await _serverTransactions.SendResponseAsync(
                            _initialInvite,
                            _remoteEndPoint,
                            _signalingTransport,
                            statusCode: 486,
                            reasonPhrase: "Busy Here",
                            rejectHeaders,
                            body: null,
                            ct)
                        .ConfigureAwait(false);
                }
                TransitionTo(
                    SipDialogState.Terminated,
                    reason ?? SipReasonHeader.CreateSipStatusReason(486, "Busy Here"));
                return;
            }
            if (!_isInbound && State is SipDialogState.Inviting or SipDialogState.Ringing)
            {
                await _transactionService.SendCancelAsync(ct, reason).ConfigureAwait(false);
                TransitionTo(
                    SipDialogState.Terminated,
                    reason ?? SipReasonHeader.CreateSipStatusReason(487, "Request Terminated"));
                return;
            }
            if (State is SipDialogState.Established or SipDialogState.OnHold)
            {
                // RFC 3261 §9.1: if a re-INVITE transaction is in flight, send CANCEL for it;
                // otherwise send BYE to terminate the established dialog.
                if (_activeInviteCSeq > 0 && !string.IsNullOrWhiteSpace(_activeInviteBranch))
                    await _transactionService.SendCancelAsync(ct, reason).ConfigureAwait(false);
                else
                    await _transactionService.SendByeAsync(ct, reason).ConfigureAwait(false);
                TransitionTo(SipDialogState.Terminated, reason);
                return;
            }
            TransitionTo(SipDialogState.Terminated, reason);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <inheritdoc />
    public async Task RedirectAsync(
        IReadOnlyList<string> contactUris,
        int statusCode = 302,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (contactUris is null || contactUris.Count == 0)
            throw new ArgumentException("At least one Contact URI is required for redirect.", nameof(contactUris));
        if (statusCode < 300 || statusCode > 399)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Redirect status code must be 3xx (300–399).");
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == SipDialogState.Terminated) return;
            if (!_isInbound || State != SipDialogState.Ringing)
                throw new InvalidOperationException(
                    $"RedirectAsync is only valid for inbound dialogs in Ringing state; current state is {State}.");
            if (_initialInvite is null || string.IsNullOrWhiteSpace(_localTag))
                throw new InvalidOperationException("Inbound INVITE context is missing.");
            // RFC 3261 §8.3: Build 3xx response from inbound INVITE.
            // Record-Route MUST NOT be forwarded in a redirect response (§8.3).
            // Contact header carries the redirect targets, NOT the local contact.
            var redirectHeaders = _headerService.CreateResponseHeadersFromRequest(
                _initialInvite,
                _localTag,
                includeContentType: false);
            redirectHeaders.Remove("Record-Route");
            redirectHeaders["Contact"] = string.Join(", ",
                contactUris.Select(u => u.TrimStart('<').TrimEnd('>') is var stripped && u.Contains('<')
                    ? u
                    : $"<{u}>"));
            var reasonPhrase = statusCode switch
            {
                300 => "Multiple Choices",
                301 => "Moved Permanently",
                302 => "Moved Temporarily",
                305 => "Use Proxy",
                380 => "Alternative Service",
                _ => "Redirect"
            };
            await _serverTransactions.SendResponseAsync(
                    _initialInvite,
                    _remoteEndPoint,
                    _signalingTransport,
                    statusCode,
                    reasonPhrase,
                    redirectHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            TransitionTo(
                SipDialogState.Terminated,
                SipReasonHeader.CreateSipStatusReason(statusCode, reasonPhrase));
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <inheritdoc />
    public async Task HoldAsync(
        string? sessionDescription = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        string? body;
        try
        {
            if (State != SipDialogState.Established)
                throw new InvalidOperationException($"Dialog must be Established, current state is {State}.");
            body = sessionDescription
                ?? _sdpProvider.BuildOffer(new System.Net.IPEndPoint(LocalSignalingEndPoint.Address, 0), true);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
        await _transactionService.SendInviteTransactionAsync(
                body,
                allowRingingTransition: false,
                successState: SipDialogState.OnHold,
                ct)
            .ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task UnholdAsync(
        string? sessionDescription = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        string? body;
        try
        {
            if (State != SipDialogState.OnHold)
                throw new InvalidOperationException($"Dialog must be OnHold, current state is {State}.");
            body = sessionDescription
                ?? _sdpProvider.BuildOffer(new System.Net.IPEndPoint(LocalSignalingEndPoint.Address, 0), false);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
        await _transactionService.SendInviteTransactionAsync(
                body,
                allowRingingTransition: false,
                successState: SipDialogState.Established,
                ct)
            .ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task SendDtmfAsync(
        char digit,
        int durationMs = 160,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!SipCallSessionUtilities.IsValidDtmfDigit(digit))
            throw new ArgumentException($"Invalid DTMF digit '{digit}'. Valid digits: 0-9, *, #, A-D.", nameof(digit));
        if (durationMs < 40)
            throw new ArgumentOutOfRangeException(nameof(durationMs), durationMs, "DTMF duration must be at least 40 ms.");
        var body = $"Signal={char.ToUpperInvariant(digit)}\r\nDuration={durationMs}";
        await SendInfoAsync("application/dtmf-relay", body, ct).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task SendInfoAsync(
        string contentType,
        string body,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("Content-Type is required for SIP INFO.", nameof(contentType));
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Body is required for SIP INFO.", nameof(body));
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (SipDialogState.Established or SipDialogState.OnHold))
                throw new InvalidOperationException($"Dialog must be Established or OnHold, current state is {State}.");
            await _transactionService.SendInfoAsync(contentType, body, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <inheritdoc />
    public async Task<bool> SendReferAsync(
        string referTo,
        string? referredBy = null,
        bool suppressSubscription = false,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(referTo))
            throw new ArgumentException("referTo is required for SIP REFER.", nameof(referTo));
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (SipDialogState.Established or SipDialogState.OnHold))
                throw new InvalidOperationException($"Dialog must be Established or OnHold, current state is {State}.");
            return await _transactionService.SendReferAsync(referTo, referredBy, suppressSubscription, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <inheritdoc />
    public async Task<bool> SendOptionsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (SipDialogState.Established or SipDialogState.OnHold or SipDialogState.Ringing))
                throw new InvalidOperationException($"Dialog must be active for OPTIONS, current state is {State}.");
            return await _transactionService.SendOptionsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <inheritdoc />
    public async Task<bool> SendSubscribeAsync(
        string eventType,
        int expiresSeconds = 300,
        string? acceptHeader = null,
        string? body = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("eventType is required for SIP SUBSCRIBE.", nameof(eventType));
        if (expiresSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(expiresSeconds), "expiresSeconds must be >= 0.");
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (SipDialogState.Established or SipDialogState.OnHold or SipDialogState.Ringing))
                throw new InvalidOperationException($"Dialog must be active for SUBSCRIBE, current state is {State}.");
            return await _transactionService.SendSubscribeAsync(
                    eventType,
                    expiresSeconds,
                    acceptHeader,
                    body,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <summary>
    /// Sends in-dialog NOTIFY for an active subscription (RFC 6665 §4.2.2).
    /// </summary>
    public async Task<bool> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType = null,
        string? body = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("eventType is required for NOTIFY.", nameof(eventType));
        if (string.IsNullOrWhiteSpace(subscriptionState))
            throw new ArgumentException("subscriptionState is required for NOTIFY.", nameof(subscriptionState));
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (SipDialogState.Established or SipDialogState.OnHold or SipDialogState.Ringing))
                throw new InvalidOperationException($"Dialog must be active for NOTIFY, current state is {State}.");
            return await _transactionService.SendNotifyAsync(
                    eventType,
                    subscriptionState,
                    contentType,
                    body,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
    }
    /// <summary>
    /// Handles inbound SIP request for this dialog.
    /// </summary>
    internal Task HandleInboundRequestAsync(
        IPEndPoint remoteEndPoint,
        SipRequest request,
        CancellationToken ct) =>
        _inboundService.HandleInboundRequestAsync(remoteEndPoint, request, ct);
    /// <summary>
    /// Handles inbound SIP response for this dialog.
    /// </summary>
    internal void HandleInboundResponse(IPEndPoint remoteEndPoint, SipResponse response) =>
        _transactionService.HandleInboundResponse(remoteEndPoint, response);
    /// <summary>
    /// Returns true when one Replaces header targets this dialog.
    /// </summary>
    internal bool MatchesReplacesTarget(SipReplacesHeaderValue replaces)
    {
        ArgumentNullException.ThrowIfNull(replaces);
        lock (_sync)
            return replaces.MatchesDialog(CallId, _localTag, _remoteTag);
    }
    /// <summary>
    /// Increments and returns next local CSeq value.
    /// </summary>
    internal int NextLocalCSeq()
    {
        lock (_sync)
        {
            _localCSeq++;
            return _localCSeq;
        }
    }
    /// <summary>
    /// Applies state transition and raises state event.
    /// </summary>
    internal void TransitionTo(
        SipDialogState next,
        SipDialogTerminationReason? terminationReason = null)
    {
        SipDialogState old;
        SipDialogTerminationReason? effectiveTerminationReason;
        lock (_sync)
        {
            old = _state;
            if (old == next || old == SipDialogState.Terminated)
                return;
            _state = next;
            if (next == SipDialogState.Terminated && terminationReason is not null)
                _lastTerminationReason = terminationReason;
            effectiveTerminationReason = next == SipDialogState.Terminated ? _lastTerminationReason : null;
        }
        _logger.LogDebug(
            "SIP session {CallId}: {Old} -> {New}{Reason}", CallId, old, next,
            effectiveTerminationReason is { } r ? $" (reason: {r.Protocol} {r.Cause} {r.Text})" : string.Empty);
        if (next == SipDialogState.Terminated)
            _sessionTimerManager.Stop();
        StateChanged?.Invoke(this, new SipDialogStateChangedEventArgs(old, next, effectiveTerminationReason));
    }
    /// <summary>
    /// Raises DTMF event with parser-decoded tone metadata.
    /// </summary>
    internal void RaiseDtmfReceived(byte toneCode, int durationMilliseconds)
    {
        try
        {
            DtmfReceived?.Invoke(this, new SipDtmfReceivedEventArgs(toneCode, durationMilliseconds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session {CallId}: DTMF callback failed.", CallId);
        }
    }
    /// <summary>
    /// Raises transfer-request event and returns caller acceptance.
    /// </summary>
    internal bool RaiseTransferRequested(string referTo, string referredBy)
    {
        if (TransferRequested is null)
            return false;
        var args = new SipTransferRequestedEventArgs(referTo, referredBy);
        try
        {
            TransferRequested?.Invoke(this, args);
            return args.Accept;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session {CallId}: transfer callback failed.", CallId);
            return false;
        }
    }
    /// <summary>
    /// Raises subscription-request event and returns caller acceptance.
    /// </summary>
    internal bool RaiseSubscriptionRequested(string eventType, int expiresSeconds, string? acceptHeader)
    {
        // Default to acceptance when no app callback is registered so SUBSCRIBE lifecycle
        // remains RFC-friendly and deterministic in headless/SDK-only integrations.
        if (SubscriptionRequested is null)
            return true;
        var args = new SipSubscriptionRequestedEventArgs(eventType, expiresSeconds, acceptHeader);
        try
        {
            SubscriptionRequested?.Invoke(this, args);
            return args.Accept;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session {CallId}: subscription callback failed.", CallId);
            return false;
        }
    }
    /// <summary>
    /// Raises inbound NOTIFY event to application.
    /// </summary>
    internal void RaiseNotifyReceived(string eventType, string subscriptionState, bool isTerminated, string? contentType, string? body)
    {
        try
        {
            NotifyReceived?.Invoke(
                this,
                new SipNotifyReceivedEventArgs(eventType, subscriptionState, isTerminated, contentType, body));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session {CallId}: NOTIFY callback failed.", CallId);
        }
    }
    /// <summary>
    /// Applies negotiated session timer values when Session-Expires is available.
    /// </summary>
    internal void ApplySessionTimerNegotiation(string? sessionExpiresHeader, bool localIsRequester)
    {
        if (!SipSessionTimerPolicy.TryResolveNegotiation(
                sessionExpiresHeader,
                localIsRequester,
                out var intervalSeconds,
                out var localIsRefresher))
        {
            return;
        }
        _sessionTimerManager.ApplyNegotiation(intervalSeconds, localIsRefresher);
    }
    /// <summary>
    /// Sends one in-dialog UPDATE refresh attempt for local-refresher session timers.
    /// </summary>
    private async Task<bool> SendSessionRefreshAsync(CancellationToken ct)
        => await SipCallSessionUtilities.SendSessionRefreshAsync(
                _operationGate,
                () => Volatile.Read(ref _disposed) != 0,
                () => State,
                _transactionService.SendSessionRefreshUpdateAsync,
                ReleaseOperationGateSafe,
                CallId,
                _logger,
                ct)
            .ConfigureAwait(false);
    /// <summary>
    /// Handles negotiated session timeout by terminating the dialog.
    /// </summary>
    private async Task HandleSessionTimerExpiredAsync(CancellationToken ct)
        => await SipCallSessionUtilities.HandleSessionTimerExpiredAsync(
                _operationGate,
                () => Volatile.Read(ref _disposed) != 0,
                () => State,
                token => _transactionService.SendByeAsync(token),
                TransitionTo,
                ReleaseOperationGateSafe,
                CallId,
                _logger,
                ct)
            .ConfigureAwait(false);
    private async Task<bool> SendReliableProvisionalAndWaitForPrackAsync(
        SipRequest invite,
        string localTag,
        CancellationToken ct)
        => await SipCallSessionUtilities.SendReliableProvisionalAndWaitForPrackAsync(
                invite,
                localTag,
                CallId,
                _reliableProvisionalManager,
                _headerService,
                _serverTransactions,
                _remoteEndPoint,
                _signalingTransport,
                _logger,
                _timeout,
                ReliableProvisionalT1,
                ReliableProvisionalT2,
                ct)
            .ConfigureAwait(false);
    /// <summary>
    /// Throws when session is disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipCallSession));
    }
    /// <summary>
    /// Releases operation semaphore safely when disposal races with in-flight operations.
    /// </summary>
    private void ReleaseOperationGateSafe()
    {
        if (_disposed != 0) return;
        try
        {
            _operationGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Narrow race between disposed check and release — safe to ignore.
        }
    }
    /// <summary>
    /// Disposes the session and internal resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _reliableProvisionalManager.Dispose();
        _sessionTimerManager.Dispose();
        _inboundService.Dispose();
        _operationGate.Dispose();
    }
    internal void NotifyRemoteHoldChangedContext(bool isOnHold) =>
        RemoteHoldChanged?.Invoke(this, isOnHold);
    internal void ApplyInviteDialogResponse(SipResponse response)
    {
        _dialogManager.ApplyInviteResponse(response, RemoteUri);
        var tag = _dialogManager.ConfirmedRemoteTag;
        if (!string.IsNullOrWhiteSpace(tag))
            lock (_sync) _remoteTag = tag;
    }
    internal void ApplyInboundDialogRequest(SipRequest request)
    {
        _dialogManager.ApplyInboundRequest(request, RemoteUri);
        var tag = _dialogManager.ConfirmedRemoteTag;
        if (tag is not null)
            lock (_sync) _remoteTag ??= tag;
    }
    internal void ApplyTargetRefreshDialogResponse(SipResponse response, string method) =>
        _dialogManager.ApplyTargetRefreshResponse(response, method, RemoteUri);
    internal void SetRemoteSdp(string? sdp)
    {
        lock (_sync) { _remoteSdp = sdp; }
    }
    internal void SetLocalSdp(string? sdp)
    {
        lock (_sync) { _localSdp = sdp; }
    }
    internal bool TryAcknowledgeReliableProvisional(
        string? rackHeader,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase) =>
        _reliableProvisionalManager.TryAcknowledge(
            rackHeader,
            out rejectionStatusCode,
            out rejectionReasonPhrase);
    internal bool TryValidateInboundCSeq(
        SipRequest request,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase,
        out int? retryAfterSeconds)
        => SipCallSessionUtilities.TryValidateInboundCSeq(
            _sync,
            ref _lastRemoteCSeq,
            ref _hasRemoteCSeq,
            request,
            out rejectionStatusCode,
            out rejectionReasonPhrase,
            out retryAfterSeconds);
    /// <summary>
    /// Applies remote asserted identity from trusted peers only.
    /// </summary>
    internal void ApplyRemoteAssertedIdentity(
        string? assertedIdentityHeader,
        IPEndPoint remoteEndPoint)
        => SipCallSessionUtilities.ApplyRemoteAssertedIdentity(
            _identityTrustPolicy,
            _signalingTransport,
            _sync,
            ref _remoteAssertedIdentity,
            assertedIdentityHeader,
            remoteEndPoint,
            _logger,
            CallId);
}
