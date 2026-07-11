using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Handles additional successful INVITE responses arriving from forked branches (RFC 3261 §13.2):
/// ACKs each matching 2xx and sends one BYE per non-selected dialog. Constructed with the call
/// session context it operates on, so it is reusable per session and injected as a collaborator.
/// </summary>
internal sealed class SipForkedInviteHandler
{
    private readonly ISipCallSessionContext _context;
    private readonly object _sync = new();
    private readonly HashSet<string> _terminatedForkedInviteTags = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a forked-INVITE handler bound to one call session context.
    /// </summary>
    public SipForkedInviteHandler(ISipCallSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Handles a successful INVITE response that may belong to a forked branch: ACKs the branch and,
    /// for a non-selected dialog, sends one BYE. No-op for non-2xx / non-INVITE / selected-dialog and
    /// while the INVITE transaction is still active (ACK is owned by the transaction flow there).
    /// </summary>
    public void HandleSuccessResponse(SipResponse response, IPEndPoint remoteEndPoint)
    {
        if (!TryGetInviteSuccessForkCandidate(response, out var inviteCseq, out var remoteTag))
            return;

        // During an active INVITE transaction, ACK handling is owned by the transaction flow itself.
        // Fork handling is only applied after the INVITE transaction has completed.
        if (!string.IsNullOrWhiteSpace(_context.ActiveInviteBranch))
            return;

        var selectedRemoteTag = _context.RemoteTag;
        if (string.IsNullOrWhiteSpace(selectedRemoteTag))
            return;

        var isSelectedDialog = string.Equals(remoteTag, selectedRemoteTag, StringComparison.Ordinal);
        var shouldSendBye = false;
        if (!isSelectedDialog)
        {
            lock (_sync)
            {
                if (_terminatedForkedInviteTags.Add(remoteTag))
                    shouldSendBye = true;
            }
        }

        _ = AcknowledgeForkAndMaybeTerminateAsync(
            response,
            inviteCseq,
            remoteTag,
            remoteEndPoint,
            shouldSendBye);
    }

    /// <summary>
    /// Sends ACK for one forked INVITE 2xx response and optionally sends BYE for non-selected branch.
    /// </summary>
    private async Task AcknowledgeForkAndMaybeTerminateAsync(
        SipResponse response,
        int inviteCseq,
        string remoteTag,
        IPEndPoint remoteEndPoint,
        bool sendBye)
    {
        try
        {
            await SendForkAckAsync(response, inviteCseq, remoteTag, remoteEndPoint, CancellationToken.None)
                .ConfigureAwait(false);

            if (!sendBye)
                return;

            await SendForkByeAsync(response, remoteTag, remoteEndPoint, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed fork ACK leaves the branch's 2xx retransmitting until it times out; a
            // failed fork BYE leaves a dangling call leg on the non-selected UAS. Both are
            // operationally significant, so surface them at Warning rather than hiding at Debug.
            _context.Logger.LogWarning(
                ex,
                "Failed handling forked INVITE success response on {CallId}.",
                _context.CallId);
        }
    }

    /// <summary>
    /// Sends ACK for one specific INVITE success response fork.
    /// </summary>
    private async Task SendForkAckAsync(
        SipResponse response,
        int inviteCseq,
        string remoteTag,
        IPEndPoint remoteEndPoint,
        CancellationToken ct)
    {
        var requestUri = ResolveForkRequestUri(response);
        var routeSet = SipCallSessionTransactionUtilities.ParseRouteSetFromRecordRoute(response.Header("Record-Route"));
        var headers = CreateForkDialogRequestHeaders(
            response,
            method: "ACK",
            cseq: inviteCseq,
            remoteTag,
            branch: SipProtocol.NewBranch(),
            routeSet,
            remoteEndPoint);

        await _context.Transport.SendRequestAsync(
                "ACK",
                requestUri,
                headers,
                body: null,
                remoteEndPoint,
                _context.SignalingTransport,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends BYE for one non-selected forked dialog after successful ACK.
    /// </summary>
    private async Task SendForkByeAsync(
        SipResponse response,
        string remoteTag,
        IPEndPoint remoteEndPoint,
        CancellationToken ct)
    {
        var requestUri = ResolveForkRequestUri(response);
        var routeSet = SipCallSessionTransactionUtilities.ParseRouteSetFromRecordRoute(response.Header("Record-Route"));
        var cseq = _context.NextLocalCSeq();
        var headers = CreateForkDialogRequestHeaders(
            response,
            method: "BYE",
            cseq,
            remoteTag,
            branch: SipProtocol.NewBranch(),
            routeSet,
            remoteEndPoint);

        await _context.Transport.SendRequestAsync(
                "BYE",
                requestUri,
                headers,
                body: null,
                remoteEndPoint,
                _context.SignalingTransport,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true when an inbound response represents one successful INVITE branch for active transaction.
    /// </summary>
    private bool TryGetInviteSuccessForkCandidate(
        SipResponse response,
        out int inviteCseq,
        out string remoteTag)
    {
        inviteCseq = 0;
        remoteTag = string.Empty;
        if (!SipProtocol.IsSuccess(response.StatusCode))
            return false;

        var cseqHeader = response.Header("CSeq");
        var cseqMethod = SipProtocol.ExtractCSeqMethod(cseqHeader);
        if (!string.Equals(cseqMethod, "INVITE", StringComparison.Ordinal))
            return false;

        inviteCseq = SipProtocol.ExtractCSeqNumber(cseqHeader);
        if (inviteCseq <= 0 || inviteCseq != _context.ActiveInviteCSeq)
            return false;

        remoteTag = SipProtocol.ExtractTag(response.Header("To")) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(remoteTag))
            return false;

        return true;
    }

    /// <summary>
    /// Creates request headers bound to one explicit fork dialog identity.
    /// </summary>
    private Dictionary<string, string> CreateForkDialogRequestHeaders(
        SipResponse response,
        string method,
        int cseq,
        string remoteTag,
        string branch,
        IReadOnlyList<string> routeSet,
        IPEndPoint remoteEndPoint)
    {
        if (string.IsNullOrWhiteSpace(_context.LocalTag))
            throw new InvalidOperationException("Local tag is missing.");

        var localEndPoint = _context.Transport.GetLocalEndPoint(_context.SignalingTransport);
        var advertisedLocalEndPoint = LocalEndPointAdvertisementResolver.ResolveAdvertisedLocalEndPoint(
            localEndPoint,
            remoteEndPoint);
        var localUser = SipProtocol.TryParseSipUri(_context.LocalUri, out var parsedUser, out _, out _)
            ? parsedUser
            : "user";
        var contactUri = SipSignalingFormat.BuildContactUri(localUser, advertisedLocalEndPoint, _context.SignalingTransport);

        var toHeader = response.Header("To");
        if (string.IsNullOrWhiteSpace(toHeader))
            toHeader = SipProtocol.FormatNameAddr(displayName: null, _context.RemoteUri, remoteTag);

        var fromHeader = response.Header("From");
        if (string.IsNullOrWhiteSpace(fromHeader))
            fromHeader = SipProtocol.FormatNameAddr(_context.LocalDisplayName, _context.LocalUri, _context.LocalTag);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = SipSignalingFormat.BuildVia(advertisedLocalEndPoint, branch, _context.SignalingTransport),
            ["Max-Forwards"] = "70",
            ["From"] = fromHeader,
            ["To"] = toHeader,
            ["Call-ID"] = _context.CallId,
            ["CSeq"] = $"{cseq} {method}",
            ["Contact"] = $"<{contactUri}>",
            ["User-Agent"] = _context.UserAgent,
            ["X-CalloraVoipSdk-Trace-Id"] = _context.CallId
        };

        if (routeSet.Count > 0)
            headers["Route"] = string.Join(", ", routeSet.Select(uri => $"<{uri}>"));

        return headers;
    }

    /// <summary>
    /// Resolves one explicit request target URI for a forked INVITE success response.
    /// </summary>
    private string ResolveForkRequestUri(SipResponse response)
    {
        var contactUri = SipProtocol.ExtractUriFromNameAddr(response.Header("Contact"));
        if (!string.IsNullOrWhiteSpace(contactUri))
            return contactUri;

        return _context.RemoteUri;
    }
}
