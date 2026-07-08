using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed class AckTestSipCallSessionContext : ISipCallSessionContext
{
    private int _nextLocalCSeq = 7;

    public AckTestSipCallSessionContext(ISipTransportRuntime transport)
    {
        Transport = transport;
    }

    public ISipTransportRuntime Transport { get; }

    public SipSessionSdpProvider SdpProvider { get; } = new()
    {
        BuildOffer = (_, _) => string.Empty,
        TryNegotiateAnswer = (_, _, _) => string.Empty,
        TryParseMediaParameters = (_, _) => null,
        IsRemoteHold = _ => false
    };

    public ISipDigestAuthenticator DigestAuthenticator { get; } = new NoopSipDigestAuthenticator();

    public ILogger Logger { get; } = NullLogger.Instance;

    public ISipServerTransactionEngine ServerTransactions { get; } = new NoopSipServerTransactionEngine();

    public string CallId { get; } = "call-ack-test";

    public string LocalUri { get; } = "sip:alice@example.test";

    public string RemoteUri { get; } = "sip:bob@example.test";

    public string RemoteRequestUri { get; } = "sip:bob@example.test";

    public IReadOnlyList<string> RouteSet { get; } = Array.Empty<string>();

    public bool IsInbound => false;

    public SipDialogState State { get; private set; } = SipDialogState.Established;

    public string LocalDisplayName { get; } = "Alice";

    public string AuthUsername { get; } = "alice";

    public string? AuthPassword => null;

    public string UserAgent { get; } = "CalloraVoipSdk.Tests";

    public string? PreferredIdentityUri => null;

    public string? PrivacyHeader => null;

    public string? RequireHeader => null;

    public string? ProxyRequireHeader => null;

    public string? ReferredBy => null;

    public SipTransportProtocol SignalingTransport { get; } = SipTransportProtocol.Udp;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(1);

    public SipRequest? InitialInvite => null;

    public IPEndPoint RemoteEndPoint { get; set; } = new(IPAddress.Parse("192.0.2.10"), 5060);

    public string? AdvertisedPublicHost { get; set; }

    public int? AdvertisedPublicPort { get; set; }

    public string? LocalTag { get; set; } = "local-tag";

    public string? RemoteTag { get; set; }

    public int ActiveInviteCSeq { get; set; }

    public string? ActiveInviteBranch { get; set; }

    public bool HasPendingLocalInviteTransaction => ActiveInviteCSeq > 0 && !string.IsNullOrWhiteSpace(ActiveInviteBranch);

    public bool IsDisposed => false;

    public int NextLocalCSeq() => Interlocked.Increment(ref _nextLocalCSeq);

    public void TransitionTo(
        SipDialogState next,
        SipDialogTerminationReason? terminationReason = null)
    {
        State = next;
    }

    public void NotifyRemoteHoldChanged(bool isOnHold)
    {
    }

    public void NotifyDtmfReceived(byte toneCode, int durationMilliseconds)
    {
    }

    public bool NotifyTransferRequested(string referTo, string referredBy) => false;

    public bool NotifySubscriptionRequested(string eventType, int expiresSeconds, string? acceptHeader) => false;

    public void NotifyNotifyReceived(
        string eventType,
        string subscriptionState,
        bool isTerminated,
        string? contentType,
        string? body)
    {
    }

    public void TryApplyRemoteAssertedIdentity(string? assertedIdentityHeader, IPEndPoint remoteEndPoint)
    {
    }

    public void ApplyInviteDialogResponse(SipResponse response)
    {
    }

    public void ApplyInboundDialogRequest(SipRequest request)
    {
    }

    public void ApplyTargetRefreshDialogResponse(SipResponse response, string method)
    {
    }

    public void ApplySessionTimerNegotiation(string? sessionExpiresHeader, bool localIsRequester)
    {
    }

    public void SetRemoteSdp(string? sdp)
    {
    }

    public bool TryAcknowledgeReliableProvisional(
        string? rackHeader,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase)
    {
        rejectionStatusCode = 0;
        rejectionReasonPhrase = string.Empty;
        return true;
    }

    public bool TryValidateInboundCSeq(
        SipRequest request,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase,
        out int? retryAfterSeconds)
    {
        rejectionStatusCode = 0;
        rejectionReasonPhrase = string.Empty;
        retryAfterSeconds = null;
        return true;
    }
}
