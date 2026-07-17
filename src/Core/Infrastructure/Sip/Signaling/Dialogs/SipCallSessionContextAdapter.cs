using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

internal sealed class SipCallSessionContextAdapter : ISipCallSessionContext
{
    private readonly SipCallSession _session;

    internal SipCallSessionContextAdapter(SipCallSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public ISipTransportRuntime Transport => _session._transport;

    public string CallId => _session.CallId;

    public string LocalUri => _session.LocalUri;

    public string RemoteUri => _session.RemoteUri;

    public bool IsInbound => _session.IsInbound;

    public SipDialogState State => _session.State;

    public SipSessionSdpProvider SdpProvider => _session._sdpProvider;

    public ISipDigestAuthenticator DigestAuthenticator => _session._digestAuthenticator;

    public ILogger Logger => _session._logger;

    public ISipServerTransactionEngine ServerTransactions => _session._serverTransactions;

    public string LocalDisplayName => _session._localDisplayName;

    public string AuthUsername => _session._authUsername;

    public string? AuthPassword => _session._authPassword;

    public string UserAgent => _session._userAgent;

    public string? PreferredIdentityUri => _session._preferredIdentityUri;

    public string? PrivacyHeader => _session._privacyHeader;

    public string? RequireHeader => _session._requireHeader;

    public string? ProxyRequireHeader => _session._proxyRequireHeader;

    public string? ReferredBy => _session._referredBy;

    public IReadOnlyDictionary<string, string>? CustomHeaders => _session._customHeaders;

    public SipTransportProtocol SignalingTransport => _session._signalingTransport;

    public TimeSpan Timeout => _session._timeout;

    public SipRequest? InitialInvite => _session._initialInvite;

    // Read the advertised contact under _sync so host+port are observed as the atomic pair the
    // session publishes them as (HARD-C1); int? is not atomically readable on its own.
    public string? AdvertisedPublicHost { get { lock (_session._sync) return _session._advertisedPublicHost; } }

    public int? AdvertisedPublicPort { get { lock (_session._sync) return _session._advertisedPublicPort; } }

    // Single-lock snapshot of the pair — the only correct way for a caller to read host+port
    // together, since two separate property reads span two lock scopes and can straddle a write.
    public (string? Host, int? Port) AdvertisedPublicContact
    {
        get { lock (_session._sync) return (_session._advertisedPublicHost, _session._advertisedPublicPort); }
    }

    public IPEndPoint RemoteEndPoint
    {
        get { lock (_session._sync) return _session._remoteEndPoint; }
        set { lock (_session._sync) _session._remoteEndPoint = value; }
    }

    public string? LocalTag
    {
        get { lock (_session._sync) return _session._localTag; }
        set { lock (_session._sync) _session._localTag = value; }
    }

    public string? RemoteTag
    {
        get { lock (_session._sync) return _session._remoteTag; }
        set { lock (_session._sync) _session._remoteTag = value; }
    }

    public int ActiveInviteCSeq
    {
        get { lock (_session._sync) return _session._activeInviteCSeq; }
        set { lock (_session._sync) _session._activeInviteCSeq = value; }
    }

    public string? ActiveInviteBranch
    {
        get { lock (_session._sync) return _session._activeInviteBranch; }
        set { lock (_session._sync) _session._activeInviteBranch = value; }
    }

    public bool HasPendingLocalInviteTransaction
    {
        get
        {
            lock (_session._sync)
                return _session._activeInviteCSeq > 0 && !string.IsNullOrWhiteSpace(_session._activeInviteBranch);
        }
    }

    public bool IsDisposed => Volatile.Read(ref _session._disposed) != 0;

    public int NextLocalCSeq() => _session.NextLocalCSeq();

    public void TransitionTo(SipDialogState next, SipDialogTerminationReason? terminationReason) =>
        _session.TransitionTo(next, terminationReason);

    public void NotifyRemoteHoldChanged(bool isOnHold) => _session.NotifyRemoteHoldChangedContext(isOnHold);

    public void NotifyDtmfReceived(byte toneCode, int durationMilliseconds) =>
        _session.RaiseDtmfReceived(toneCode, durationMilliseconds);

    public bool NotifyTransferRequested(string referTo, string referredBy) =>
        _session.RaiseTransferRequested(referTo, referredBy);

    public bool NotifySubscriptionRequested(string eventType, int expiresSeconds, string? acceptHeader) =>
        _session.RaiseSubscriptionRequested(eventType, expiresSeconds, acceptHeader);

    public void NotifyNotifyReceived(string eventType, string subscriptionState, bool isTerminated, string? contentType, string? body) =>
        _session.RaiseNotifyReceived(eventType, subscriptionState, isTerminated, contentType, body);

    public void TryApplyRemoteAssertedIdentity(string? assertedIdentityHeader, IPEndPoint remoteEndPoint) =>
        _session.ApplyRemoteAssertedIdentity(assertedIdentityHeader, remoteEndPoint);

    public string RemoteRequestUri => _session._dialogManager.RemoteTargetUri ?? _session._initialRequestUri;

    public IReadOnlyList<string> RouteSet =>
        _session._dialogManager.RouteSet.Count > 0 ? _session._dialogManager.RouteSet : _session._initialRouteSet;

    public void ApplyInviteDialogResponse(SipResponse response) => _session.ApplyInviteDialogResponse(response);

    public void ApplyInboundDialogRequest(SipRequest request) => _session.ApplyInboundDialogRequest(request);

    public void ApplyTargetRefreshDialogResponse(SipResponse response, string method) =>
        _session.ApplyTargetRefreshDialogResponse(response, method);

    public void ApplySessionTimerNegotiation(string? sessionExpiresHeader, bool localIsRequester) =>
        _session.ApplySessionTimerNegotiation(sessionExpiresHeader, localIsRequester);

    public void SetRemoteSdp(string? sdp) => _session.SetRemoteSdp(sdp);

    public void SetLocalSdp(string? sdp) => _session.SetLocalSdp(sdp);

    public bool TryAcknowledgeReliableProvisional(
        string? rackHeader,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase) =>
        _session.TryAcknowledgeReliableProvisional(rackHeader, out rejectionStatusCode, out rejectionReasonPhrase);

    public bool TryValidateInboundCSeq(
        SipRequest request,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase,
        out int? retryAfterSeconds) =>
        _session.TryValidateInboundCSeq(request, out rejectionStatusCode, out rejectionReasonPhrase, out retryAfterSeconds);
}
