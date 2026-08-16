using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Internal runtime context contract for SIP call-session helper services.
/// Keeps session orchestration in <see cref="SipCallSession"/> while allowing
/// inbound, transaction, and header concerns to live in dedicated classes.
/// </summary>
internal interface ISipCallSessionContext
{
    /// <summary>
    /// Transport runtime used for SIP wire I/O.
    /// </summary>
    ISipTransportRuntime Transport { get; }

    /// <summary>
    /// SDP delegate provider for building and parsing offer/answer bodies.
    /// </summary>
    SipSessionSdpProvider SdpProvider { get; }

    /// <summary>
    /// Digest authenticator used for challenge-response handling.
    /// </summary>
    ISipDigestAuthenticator DigestAuthenticator { get; }

    /// <summary>
    /// Logger for diagnostics in helper services.
    /// </summary>
    ILogger Logger { get; }

    /// <summary>
    /// Server transaction engine used for inbound response dispatch semantics.
    /// </summary>
    ISipServerTransactionEngine ServerTransactions { get; }

    /// <summary>
    /// Dialog call identifier.
    /// </summary>
    string CallId { get; }

    /// <summary>
    /// Local SIP URI.
    /// </summary>
    string LocalUri { get; }

    /// <summary>
    /// Remote SIP URI.
    /// </summary>
    string RemoteUri { get; }

    /// <summary>
    /// Effective request URI used for in-dialog routing.
    /// </summary>
    string RemoteRequestUri { get; }

    /// <summary>
    /// Route set used for in-dialog Route headers. Prefers the dialog-established route set (reversed
    /// Record-Route) and falls back to the preloaded (already §12.2.1.1-planned) initial route set.
    /// </summary>
    IReadOnlyList<string> RouteSet { get; }

    /// <summary>
    /// The dialog-established route set only (reversed Record-Route), empty when the dialog carries none. Unlike
    /// <see cref="RouteSet"/> this never falls back to the preloaded initial route set, so it is always the raw
    /// route set — safe to (re)plan per RFC 3261 §12.2.1.1 without double-rewriting an already-planned set.
    /// </summary>
    IReadOnlyList<string> DialogRouteSet { get; }

    /// <summary>
    /// Indicates whether session was created from an inbound INVITE.
    /// </summary>
    bool IsInbound { get; }

    /// <summary>
    /// Current signaling state.
    /// </summary>
    SipDialogState State { get; }

    /// <summary>
    /// Local display name used in From headers.
    /// </summary>
    string LocalDisplayName { get; }

    /// <summary>
    /// Auth username used for SIP digest.
    /// </summary>
    string AuthUsername { get; }

    /// <summary>
    /// Auth password used for SIP digest.
    /// </summary>
    string? AuthPassword { get; }

    /// <summary>
    /// User-Agent header value.
    /// </summary>
    string UserAgent { get; }

    /// <summary>
    /// Optional preferred identity URI used for outbound <c>P-Preferred-Identity</c>.
    /// </summary>
    string? PreferredIdentityUri { get; }

    /// <summary>
    /// Optional <c>Privacy</c> header value applied to outbound INVITE requests (RFC 3323).
    /// </summary>
    string? PrivacyHeader { get; }

    /// <summary>
    /// Optional Require header value applied to outbound requests.
    /// </summary>
    string? RequireHeader { get; }

    /// <summary>
    /// Optional Proxy-Require header value applied to outbound requests.
    /// </summary>
    string? ProxyRequireHeader { get; }

    /// <summary>
    /// Optional <c>Referred-By</c> header value (RFC 3892) included in the initial INVITE.
    /// </summary>
    string? ReferredBy { get; }

    /// <summary>
    /// Optional consumer-supplied extra headers added to the outbound initial INVITE. Protected
    /// dialog/transport headers are ignored by the header builder. <see langword="null"/> for inbound.
    /// </summary>
    IReadOnlyDictionary<string, string>? CustomHeaders { get; }

    /// <summary>
    /// SIP transport protocol used for this dialog.
    /// </summary>
    SipTransportProtocol SignalingTransport { get; }

    /// <summary>
    /// Optional per-line TLS identity presented on this dialog's outbound TLS handshakes for mutual
    /// TLS (issue #183). The default implementation returns <see langword="null"/> (client-wide
    /// default identity); the production adapter surfaces the line's configuration.
    /// </summary>
    TlsConfiguration? LineTls => null;

    /// <summary>
    /// Transaction timeout.
    /// </summary>
    TimeSpan Timeout { get; }

    /// <summary>
    /// Initial inbound INVITE when available.
    /// </summary>
    SipRequest? InitialInvite { get; }

    /// <summary>
    /// Current remote transport endpoint for request dispatch.
    /// </summary>
    IPEndPoint RemoteEndPoint { get; set; }

    /// <summary>
    /// Optional public host (IP or FQDN) to advertise in in-dialog Contact URIs instead of
    /// the route-probed local address. Set behind NAT so a trunk peer can route the ACK to
    /// a 2xx response and subsequent in-dialog requests. <see langword="null"/> keeps local.
    /// </summary>
    string? AdvertisedPublicHost { get; }

    /// <summary>
    /// Optional public port paired with <see cref="AdvertisedPublicHost"/>.
    /// <see langword="null"/> or 0 reuses the local signaling port.
    /// </summary>
    int? AdvertisedPublicPort { get; }

    /// <summary>
    /// Reads the advertised public contact (<see cref="AdvertisedPublicHost"/> and
    /// <see cref="AdvertisedPublicPort"/>) as a single snapshot. Callers that need both values as
    /// one logical pair must use this instead of reading the two properties separately: an
    /// implementation backed by mutable state returns both under one lock, so a concurrent writer
    /// can never produce a mismatched host/port (HARD-C1). The default composition is only safe
    /// for immutable/single-threaded implementations.
    /// </summary>
    (string? Host, int? Port) AdvertisedPublicContact => (AdvertisedPublicHost, AdvertisedPublicPort);

    /// <summary>
    /// Local SIP tag.
    /// </summary>
    string? LocalTag { get; set; }

    /// <summary>
    /// Remote SIP tag.
    /// </summary>
    string? RemoteTag { get; set; }

    /// <summary>
    /// Active INVITE CSeq used by CANCEL flow.
    /// </summary>
    int ActiveInviteCSeq { get; set; }

    /// <summary>
    /// Active INVITE branch used by CANCEL flow.
    /// </summary>
    string? ActiveInviteBranch { get; set; }

    /// <summary>
    /// Reads the active INVITE CSeq and branch as one atomic snapshot. The CANCEL flow must use
    /// this instead of reading <see cref="ActiveInviteCSeq"/> and <see cref="ActiveInviteBranch"/>
    /// separately: the two are cleared together when the INVITE completes, so two separate reads can
    /// straddle that clear and build a CANCEL with a mismatched CSeq/branch (HARD-C2). The default
    /// composition is only safe for immutable/single-threaded implementations.
    /// </summary>
    (int CSeq, string? Branch) ActiveInvite => (ActiveInviteCSeq, ActiveInviteBranch);

    /// <summary>
    /// Publishes the active INVITE CSeq and branch as one atomic pair.
    /// </summary>
    void SetActiveInvite(int cseq, string? branch)
    {
        ActiveInviteCSeq = cseq;
        ActiveInviteBranch = branch;
    }

    /// <summary>
    /// Atomically clears the active INVITE state so a later CANCEL cannot fire against a completed
    /// or abandoned INVITE transaction (HARD-C2, PRACK/abnormal-exit leak).
    /// </summary>
    void ClearActiveInvite()
    {
        ActiveInviteCSeq = 0;
        ActiveInviteBranch = null;
    }

    /// <summary>
    /// CSeq of the most recent INVITE this UAC completed with a 2xx, or 0 when none has (#158 P2-10).
    /// </summary>
    /// <remarks>
    /// A forking proxy may deliver a 2xx from another branch after the first one established the dialog, and
    /// RFC 3261 §13.2.2.4 requires an ACK for each and a BYE for every dialog we do not keep. Matching those
    /// late responses needs the INVITE's CSeq — which <see cref="ClearActiveInvite"/> takes away, because the
    /// CANCEL flow must lose its target at exactly that moment. So the two are separate states: the cancel
    /// target dies with the transaction, the identity outlives it.
    /// </remarks>
    int CompletedInviteCSeq { get; }

    /// <summary>
    /// Marks the INVITE with <paramref name="cseq"/> as completed by a 2xx: clears the cancel target
    /// (HARD-C2) and keeps the CSeq as <see cref="CompletedInviteCSeq"/> so late forked 2xx responses for the
    /// same INVITE still match (#158 P2-10). Both happen under one lock — a reader must never see the cancel
    /// target gone while the identity is not yet published, or the fork window has a hole in it.
    /// </summary>
    void CompleteActiveInvite(int cseq);

    /// <summary>
    /// True when one local INVITE client transaction is currently in progress.
    /// </summary>
    bool HasPendingLocalInviteTransaction { get; }

    /// <summary>
    /// True when parent session was disposed.
    /// </summary>
    bool IsDisposed { get; }

    /// <summary>
    /// Token cancelled when the parent session is disposed. Session-scoped background work (e.g. the REFER
    /// subscription auto-timeout) links to it so it stops instead of firing into a torn-down dialog.
    /// </summary>
    CancellationToken SessionShutdownToken { get; }

    /// <summary>
    /// Increments and returns next local CSeq.
    /// </summary>
    int NextLocalCSeq();

    /// <summary>
    /// Applies one dialog-state transition.
    /// </summary>
    void TransitionTo(
        SipDialogState next,
        SipDialogTerminationReason? terminationReason = null);

    /// <summary>
    /// Raises remote hold state notification on the parent session.
    /// </summary>
    void NotifyRemoteHoldChanged(bool isOnHold);

    /// <summary>
    /// Raises DTMF notification on the parent session.
    /// </summary>
    void NotifyDtmfReceived(byte toneCode, int durationMilliseconds);

    /// <summary>
    /// Raises REFER transfer request on the parent session and returns acceptance decision. The
    /// <paramref name="subscription"/> handle is surfaced to the SDK consumer for progress reporting
    /// on the REFER's implicit subscription (RFC 3515 / RFC 6665).
    /// </summary>
    bool NotifyTransferRequested(string referTo, string referredBy, IReferSubscription subscription);

    /// <summary>
    /// Raises SUBSCRIBE request on the parent session and returns acceptance decision.
    /// </summary>
    bool NotifySubscriptionRequested(string eventType, int expiresSeconds, string? acceptHeader);

    /// <summary>
    /// Raises inbound NOTIFY event on the parent session (RFC 6665 §6.1.1).
    /// </summary>
    void NotifyNotifyReceived(string eventType, string subscriptionState, bool isTerminated, string? contentType, string? body);

    /// <summary>
    /// Applies remote asserted identity from a SIP header when the peer is trusted.
    /// </summary>
    void TryApplyRemoteAssertedIdentity(string? assertedIdentityHeader, IPEndPoint remoteEndPoint);

    /// <summary>
    /// Applies dialog updates from an INVITE response.
    /// </summary>
    void ApplyInviteDialogResponse(SipResponse response);

    /// <summary>
    /// Applies dialog updates from inbound requests.
    /// </summary>
    void ApplyInboundDialogRequest(SipRequest request);

    /// <summary>
    /// Applies dialog target refresh updates from successful responses.
    /// </summary>
    void ApplyTargetRefreshDialogResponse(SipResponse response, string method);

    /// <summary>
    /// Applies negotiated session-timer settings for this dialog.
    /// </summary>
    void ApplySessionTimerNegotiation(string? sessionExpiresHeader, bool localIsRequester);

    /// <summary>
    /// Stores the remote SDP body received from the far end.
    /// Called by <see cref="SipCallSessionTransactionService"/> when the 200 OK body arrives
    /// for outbound INVITE, and by the inbound path from the initial INVITE body.
    /// </summary>
    void SetRemoteSdp(string? sdp);

    /// <summary>
    /// Stores the SDP body carried on a provisional (180/183) response as early-media SDP, kept
    /// separate from the final <see cref="SetRemoteSdp"/> answer. This is the signaling foundation
    /// for early media (RFC 3960): a later slice sets up a receive-only media session from it. On its
    /// own this only records the SDP and does not start media — the 200 OK path is unaffected.
    /// </summary>
    void CaptureEarlyMediaSdp(string? sdp);

    /// <summary>
    /// Stores the local SDP body last put on the wire (an answer we sent on a re-INVITE, or an
    /// offer), so the media adapter can recover our own SDES key on a rekey.
    /// </summary>
    void SetLocalSdp(string? sdp);

    /// <summary>
    /// Validates and acknowledges PRACK RAck correlation for reliable provisional responses.
    /// </summary>
    bool TryAcknowledgeReliableProvisional(
        string? rackHeader,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase);

    /// <summary>
    /// Validates and tracks inbound remote CSeq ordering for this dialog.
    /// </summary>
    bool TryValidateInboundCSeq(
        SipRequest request,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase,
        out int? retryAfterSeconds);
}
