using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;
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
    private readonly SipCallSessionEventDispatcher _eventDispatcher;
    private readonly SipCallSessionInboundService _inboundService;
    private readonly SipCallSessionContextAdapter _context;
    private readonly SipReliableProvisionalManager _reliableProvisionalManager;
    private readonly SipCallSessionTimers _sessionTimers;
    // The final response to an inbound INVITE — accept, reject, or redirect (extracted, #285). It holds the
    // operation gate for the whole of each response; the dialog state stays here, behind the host delegates.
    private readonly SipInboundInviteResponder _inviteResponder;
    internal readonly SipDialogManager _dialogManager = new();
    private readonly bool _isInbound;
    // The immutable per-dialog configuration, held as the object it arrives as rather than destructured into a
    // field per value. Fifteen copies were fifteen chances to drift, and the two values that need normalising
    // (display name, initial Request-URI) now carry their rule with them instead of at each call site.
    internal readonly SipCallSessionConfiguration _config;
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
    // Outlives the cancel target above — see ISipCallSessionContext.CompletedInviteCSeq (#158 P2-10).
    internal int _completedInviteCSeq;
    private string? _remoteAssertedIdentity;
    private readonly string? _diversion;
    private readonly string? _remoteDisplayName;
    private string? _remoteSdp;
    private string? _earlyMediaSdp;
    private string? _localSdp;
    internal readonly SipSessionSdpProvider _sdpProvider;
    private SipDialogState _state;
    private SipDialogTerminationReason? _lastTerminationReason;
    internal int _disposed;
    // Cancelled on Dispose so session-scoped background work (e.g. the REFER subscription auto-timeout) stops
    // instead of firing into a torn-down dialog. Left undisposed (plain CTS, no unmanaged resource) so the token
    // stays readable after teardown.
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>Token cancelled when this session is disposed; drives session-scoped background cancellation.</summary>
    internal CancellationToken ShutdownToken => _shutdownCts.Token;
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
        _config = configuration;
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
        _eventDispatcher = new SipCallSessionEventDispatcher(_logger);
        _sdpProvider = dependencies.SdpProvider;
        _context = new SipCallSessionContextAdapter(this);
        _headerService = new SipCallSessionHeaderService(_context);
        _transactionService = new SipCallSessionTransactionService(_context, _headerService);
        _inboundService = new SipCallSessionInboundService(_context, _headerService);
        _reliableProvisionalManager = new SipReliableProvisionalManager(_logger);
        _sessionTimers = new SipCallSessionTimers(
            _operationGate,
            () => Volatile.Read(ref _disposed) != 0,
            () => State,
            _transactionService.SendSessionRefreshUpdateAsync,
            token => _transactionService.SendByeAsync(token),
            TransitionTo,
            ReleaseOperationGateSafe,
            CallId,
            _logger);
        _inviteResponder = new SipInboundInviteResponder(
            _operationGate, _headerService, _serverTransactions, _config,
            new SipInboundInviteResponderHost(
                ThrowIfDisposed,
                ReleaseOperationGateSafe,
                () => State,
                () => _isInbound,
                () => _initialInvite,
                () => { lock (_sync) return _localTag; },
                () => _remoteEndPoint,
                TransitionTo,
                ApplySessionTimerNegotiation,
                SendReliableProvisionalAndWaitForPrackAsync));
        if (_initialInvite is not null)
        {
            // For inbound sessions, the INVITE body is the remote SDP offer.
            if (!string.IsNullOrWhiteSpace(_initialInvite.Body))
                _remoteSdp = _initialInvite.Body;
            _dialogManager.ApplyInboundRequest(_initialInvite, RemoteUri);
            ApplyRemoteAssertedIdentity(
                _initialInvite.Header("P-Asserted-Identity"),
                configuration.RemoteEndPoint);
            _diversion = SipCallSessionUtilities.ParseDiversionUri(_initialInvite.Header("Diversion"));
            _remoteDisplayName = SipProtocol.ExtractDisplayNameFromNameAddr(_initialInvite.Header("From"));
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
    public string? RemoteAssertedIdentity { get { lock (_sync) return _remoteAssertedIdentity; } }
    /// <inheritdoc />
    public string? Diversion => _diversion;
    /// <inheritdoc />
    public string? RemoteDisplayName => _remoteDisplayName;
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
    /// <summary>
    /// SDP body from a provisional (180/183) response — the early-media description, kept separate
    /// from the final <see cref="RemoteSdp"/> answer. Null until a provisional carries a body.
    /// Foundation for early media; no media session is started from it in this slice.
    /// </summary>
    public string? EarlyMediaSdp
    {
        get { lock (_sync) return _earlyMediaSdp; }
    }
    /// <inheritdoc />
    public string? LocalSdp
    {
        get { lock (_sync) return _localSdp; }
    }
    /// <inheritdoc />
    public System.Net.IPEndPoint LocalSignalingEndPoint =>
        _transport.GetLocalEndPoint(_config.SignalingTransport);
    /// <inheritdoc />
    public System.Net.IPEndPoint? RemoteSignalingEndPoint
    {
        get { lock (_sync) return _remoteEndPoint; }
    }
    /// <inheritdoc />
    public void SetAdvertisedPublicContact(string? host, int? port)
    {
        // Host and port form one logical contact: publish them atomically under the same gate the
        // adapter reads them through, so a concurrent reader never observes a mismatched pair
        // (int? is a non-atomic 8-byte value — a torn read is otherwise possible). See HARD-C1.
        lock (_sync)
        {
            _advertisedPublicHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
            _advertisedPublicPort = port is > 0 ? port : null;
        }
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
            var localEndPoint = _transport.GetLocalEndPoint(_config.SignalingTransport);
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
    /// <inheritdoc />
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
                            _config.SignalingTransport,
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
                // otherwise send BYE to terminate the established dialog. Snapshot the pair under
                // _sync so the decision cannot straddle the INVITE loop clearing both fields (HARD-C2).
                bool inviteInFlight;
                lock (_sync)
                    inviteInFlight = _activeInviteCSeq > 0 && !string.IsNullOrWhiteSpace(_activeInviteBranch);
                if (inviteInFlight)
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
    /// <inheritdoc />
    public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default) =>
        SendReInviteAsync(SipDialogState.Established, sessionDescription, holdOffer: true, SipDialogState.OnHold, ct);

    /// <inheritdoc />
    public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default) =>
        SendReInviteAsync(SipDialogState.OnHold, sessionDescription, holdOffer: false, SipDialogState.Established, ct);

    /// <inheritdoc />
    public Task ReinviteAsync(string sessionDescription, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDescription);
        // Direction-preserving re-INVITE (Established → Established): the caller owns the SDP (new ICE
        // credentials), so no hold/unhold offer is built. This is the transport for an ICE restart (RFC 8445 §9).
        return SendReInviteAsync(
            SipDialogState.Established, sessionDescription, holdOffer: false, SipDialogState.Established, ct);
    }

    /// <summary>
    /// Shared in-dialog re-INVITE driver for hold/unhold/ICE-restart. Under the operation gate it asserts
    /// the <paramref name="requiredState"/> precondition and resolves the offer body — building a hold/unhold
    /// offer (<paramref name="holdOffer"/>) when the caller supplied none — then runs the INVITE transaction
    /// to <paramref name="successState"/>.
    /// </summary>
    private async Task SendReInviteAsync(
        SipDialogState requiredState,
        string? sessionDescription,
        bool holdOffer,
        SipDialogState successState,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        string? body;
        try
        {
            if (State != requiredState)
                throw new InvalidOperationException($"Dialog must be {requiredState}, current state is {State}.");
            body = sessionDescription
                ?? _sdpProvider.BuildOffer(new System.Net.IPEndPoint(LocalSignalingEndPoint.Address, 0), holdOffer);
        }
        finally
        {
            ReleaseOperationGateSafe();
        }
        await _transactionService.SendInviteTransactionAsync(
                body,
                allowRingingTransition: false,
                successState: successState,
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
            _sessionTimers.Stop();
        StateChanged?.Invoke(this, new SipDialogStateChangedEventArgs(old, next, effectiveTerminationReason));
    }
    /// <summary>
    /// Raises DTMF event with parser-decoded tone metadata.
    /// </summary>
    internal void RaiseDtmfReceived(byte toneCode, int durationMilliseconds)
        => _eventDispatcher.RaiseDtmf(DtmfReceived, this, toneCode, durationMilliseconds, CallId);

    /// <summary>
    /// Raises transfer-request event and returns caller acceptance.
    /// </summary>
    internal bool RaiseTransferRequested(string referTo, string referredBy, IReferSubscription subscription)
        => _eventDispatcher.RaiseTransferRequested(TransferRequested, this, referTo, referredBy, subscription, CallId);

    /// <summary>
    /// Raises subscription-request event and returns caller acceptance.
    /// </summary>
    internal bool RaiseSubscriptionRequested(string eventType, int expiresSeconds, string? acceptHeader)
        => _eventDispatcher.RaiseSubscriptionRequested(SubscriptionRequested, this, eventType, expiresSeconds, acceptHeader, CallId);

    /// <summary>
    /// Raises inbound NOTIFY event to application.
    /// </summary>
    internal void RaiseNotifyReceived(string eventType, string subscriptionState, bool isTerminated, string? contentType, string? body)
        => _eventDispatcher.RaiseNotifyReceived(NotifyReceived, this, eventType, subscriptionState, isTerminated, contentType, body, CallId);
    /// <summary>
    /// Applies negotiated session timer values when Session-Expires is available (RFC 4028).
    /// </summary>
    internal void ApplySessionTimerNegotiation(string? sessionExpiresHeader, bool localIsRequester)
        => _sessionTimers.ApplyNegotiation(sessionExpiresHeader, localIsRequester);
    /// <inheritdoc />
    public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default)
        => _inviteResponder.AnswerAsync(sessionDescription, ct);

    /// <inheritdoc />
    public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default)
        => _inviteResponder.RejectAsync(statusCode, reasonPhrase, ct);

    /// <inheritdoc />
    public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default)
        => _inviteResponder.RedirectAsync(contactUris, statusCode, ct);

    private async Task<bool> SendReliableProvisionalAndWaitForPrackAsync(
        SipRequest invite,
        string localTag,
        CancellationToken ct)
        => await SipCallSessionUtilities.SendReliableProvisionalAndWaitForPrackAsync(
                invite,
                localTag,
                new ReliableProvisionalSendContext
                {
                    CallId = CallId,
                    ReliableProvisionalManager = _reliableProvisionalManager,
                    HeaderService = _headerService,
                    ServerTransactions = _serverTransactions,
                    RemoteEndPoint = _remoteEndPoint,
                    SignalingTransport = _config.SignalingTransport,
                    Logger = _logger,
                    Timeout = _config.Timeout,
                    ReliableProvisionalT1 = ReliableProvisionalT1,
                    ReliableProvisionalT2 = ReliableProvisionalT2
                },
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
        _shutdownCts.Cancel();
        _reliableProvisionalManager.Dispose();
        _sessionTimers.Dispose();
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
    internal void CaptureEarlyMediaSdp(string? sdp)
    {
        lock (_sync) { _earlyMediaSdp = sdp; }
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
            _config.SignalingTransport,
            _sync,
            ref _remoteAssertedIdentity,
            assertedIdentityHeader,
            remoteEndPoint,
            _logger,
            CallId);
}
