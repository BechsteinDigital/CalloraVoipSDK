using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// The dialog state an inbound-INVITE response depends on, and the session behaviour it triggers. Passed as one
/// record so the responder can own the response policy without owning the dialog's fields — the guarded reads
/// stay behind these delegates, which take the session's gate themselves.
/// </summary>
/// <param name="ThrowIfDisposed">Fails the call if the session is already disposed.</param>
/// <param name="ReleaseOperationGate">Releases the session's operation gate, tolerating an already-released one.</param>
/// <param name="State">The current dialog state.</param>
/// <param name="IsInbound">Whether this dialog was created by an inbound INVITE.</param>
/// <param name="InitialInvite">The inbound INVITE this dialog answers, or null for an outbound dialog.</param>
/// <param name="LocalTag">The local dialog tag, required in every response To header (RFC 3261 §12.1.1).</param>
/// <param name="RemoteEndPoint">The peer's signalling address (it can move — a response follows the current one).</param>
/// <param name="TransitionTo">Moves the dialog state, optionally carrying a termination reason.</param>
/// <param name="ApplySessionTimerNegotiation">Applies the negotiated session-expiry to the refresh timers (RFC 4028).</param>
/// <param name="SendReliableProvisionalAndWaitForPrackAsync">
/// Sends a reliable provisional response and waits for its PRACK (RFC 3262); false when it goes unacknowledged.
/// </param>
internal sealed record SipInboundInviteResponderHost(
    Action ThrowIfDisposed,
    Action ReleaseOperationGate,
    Func<SipDialogState> State,
    Func<bool> IsInbound,
    Func<SipRequest?> InitialInvite,
    Func<string?> LocalTag,
    Func<IPEndPoint> RemoteEndPoint,
    Action<SipDialogState, SipDialogTerminationReason?> TransitionTo,
    Action<string?, bool> ApplySessionTimerNegotiation,
    Func<SipRequest, string, CancellationToken, Task<bool>> SendReliableProvisionalAndWaitForPrackAsync);

/// <summary>
/// Produces the final response to an inbound INVITE: accept (200), reject (4xx–6xx), or redirect (3xx). The three
/// are one concern — each runs the policy checks that apply to it and ends the INVITE server transaction exactly
/// once — and each holds the session's operation gate for its whole run, so two of them can never race a third.
/// </summary>
/// <remarks>
/// Extracted from <see cref="SipCallSession"/>, where the three together were the largest part of the file and
/// the reason it stood at the line limit (#285). The dialog state stays with the session; this owns only the
/// decision of what to answer and the order the checks run in — which is protocol-visible: RFC 3261 §8.2.2
/// Require handling precedes RFC 3262 reliable provisionals, which precede RFC 4028 session-timer validation,
/// because each can terminate the dialog before the next is reached.
/// </remarks>
internal sealed class SipInboundInviteResponder
{
    private readonly SemaphoreSlim _operationGate;
    private readonly SipCallSessionHeaderService _headerService;
    private readonly ISipServerTransactionEngine _serverTransactions;
    private readonly SipCallSessionConfiguration _config;
    private readonly SipInboundInviteResponderHost _host;

    /// <param name="operationGate">The session's single-operation gate, held for the whole of each response.</param>
    /// <param name="headerService">Builds response headers from the inbound request (Via/From/To/Call-ID/CSeq).</param>
    /// <param name="serverTransactions">Sends the response through the INVITE server transaction (RFC 3261 §17.2.1).</param>
    /// <param name="config">The dialog's immutable configuration (signalling transport, timeouts).</param>
    /// <param name="host">The dialog state and session behaviour this responder drives.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SipInboundInviteResponder(
        SemaphoreSlim operationGate,
        SipCallSessionHeaderService headerService,
        ISipServerTransactionEngine serverTransactions,
        SipCallSessionConfiguration config,
        SipInboundInviteResponderHost host)
    {
        _operationGate = operationGate ?? throw new ArgumentNullException(nameof(operationGate));
        _headerService = headerService ?? throw new ArgumentNullException(nameof(headerService));
        _serverTransactions = serverTransactions ?? throw new ArgumentNullException(nameof(serverTransactions));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task AnswerAsync(
        string? sessionDescription = null,
        CancellationToken ct = default)
    {
        _host.ThrowIfDisposed();
        if (!_host.IsInbound())
            throw new InvalidOperationException("Only inbound sessions can be answered.");
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host.State() != SipDialogState.Ringing)
                throw new InvalidOperationException($"Dialog must be Ringing, current state is {_host.State()}.");
            var invite = _host.InitialInvite()
                ?? throw new InvalidOperationException("Inbound INVITE context is missing.");
            var localTag = _host.LocalTag();
            if (string.IsNullOrWhiteSpace(localTag))
                throw new InvalidOperationException("Local tag is missing.");
            if (!SipRequireOptionPolicy.TryValidateInviteRequireHeader(
                    invite.Header("Require"),
                    out var unsupportedHeaderValue))
            {
                var unsupportedHeaders = _headerService.CreateResponseHeadersFromRequest(
                    invite,
                    localTag,
                    includeContentType: false);
                unsupportedHeaders["Unsupported"] = unsupportedHeaderValue;
                await _serverTransactions.SendResponseAsync(
                        invite,
                        _host.RemoteEndPoint(),
                        _config.SignalingTransport,
                        statusCode: 420,
                        reasonPhrase: "Bad Extension",
                        unsupportedHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
                _host.TransitionTo(SipDialogState.Terminated, null);
                return;
            }
            if (SipCallSessionUtilities.ShouldUseReliableProvisional(invite))
            {
                var prackAcknowledged = await _host.SendReliableProvisionalAndWaitForPrackAsync(
                        invite,
                        localTag,
                        ct)
                    .ConfigureAwait(false);
                if (!prackAcknowledged)
                {
                    _host.TransitionTo(SipDialogState.Terminated, null);
                    return;
                }
            }
            if (!SipSessionTimerPolicy.TryValidateInboundRequest(
                    invite,
                    out var timerRejectionCode,
                    out var timerRejectionReasonPhrase,
                    out var normalizedSessionExpires))
            {
                var timerRejectHeaders = _headerService.CreateResponseHeadersFromRequest(invite, localTag, includeContentType: false);
                if (timerRejectionCode == 422)
                    SipSessionTimerPolicy.ApplyTooSmallResponseHeaders(timerRejectHeaders);
                await _serverTransactions.SendResponseAsync(
                        invite,
                        _host.RemoteEndPoint(),
                        _config.SignalingTransport,
                        statusCode: timerRejectionCode,
                        reasonPhrase: timerRejectionReasonPhrase,
                        timerRejectHeaders,
                        body: null,
                        ct)
                    .ConfigureAwait(false);
                _host.TransitionTo(SipDialogState.Terminated, null);
                return;
            }
            var body = sessionDescription;
            var headers = _headerService.CreateResponseHeadersFromRequest(invite, localTag, includeContentType: !string.IsNullOrWhiteSpace(body));
            SipSessionTimerPolicy.ApplyResponseHeaders(headers, normalizedSessionExpires);
            await _serverTransactions.SendResponseAsync(
                    invite,
                    _host.RemoteEndPoint(),
                    _config.SignalingTransport,
                    statusCode: 200,
                    reasonPhrase: "OK",
                    headers,
                    body,
                    ct)
                .ConfigureAwait(false);
            _host.ApplySessionTimerNegotiation(
                headers.TryGetValue("Session-Expires", out var sessionExpires) ? sessionExpires : null,
                /* localIsRequester: */ false);
            _host.TransitionTo(SipDialogState.Established, null);
        }
        finally
        {
            _host.ReleaseOperationGate();
        }
    }
    public async Task RejectAsync(
        int statusCode = 486,
        string? reasonPhrase = null,
        CancellationToken ct = default)
    {
        _host.ThrowIfDisposed();
        if (statusCode < 400 || statusCode > 699)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Rejection status code must be 4xx, 5xx, or 6xx.");
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_host.IsInbound() || _host.State() != SipDialogState.Ringing)
                throw new InvalidOperationException(
                    $"RejectAsync is only valid for inbound dialogs in Ringing state; current state is {_host.State()}.");
            var invite = _host.InitialInvite();
            var localTag = _host.LocalTag();
            if (invite is null || string.IsNullOrWhiteSpace(localTag))
                throw new InvalidOperationException("Inbound INVITE context is missing.");
            var phrase = string.IsNullOrWhiteSpace(reasonPhrase)
                ? SipCallSessionUtilities.ResolveDefaultReasonPhrase(statusCode)
                : reasonPhrase;
            var rejectHeaders = _headerService.CreateResponseHeadersFromRequest(
                invite, localTag, includeContentType: false);
            await _serverTransactions.SendResponseAsync(
                    invite,
                    _host.RemoteEndPoint(),
                    _config.SignalingTransport,
                    statusCode,
                    phrase,
                    rejectHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            _host.TransitionTo(
                SipDialogState.Terminated,
                SipReasonHeader.CreateSipStatusReason(statusCode, phrase));
        }
        finally
        {
            _host.ReleaseOperationGate();
        }
    }
    public async Task RedirectAsync(
        IReadOnlyList<string> contactUris,
        int statusCode = 302,
        CancellationToken ct = default)
    {
        _host.ThrowIfDisposed();
        if (contactUris is null || contactUris.Count == 0)
            throw new ArgumentException("At least one Contact URI is required for redirect.", nameof(contactUris));
        if (statusCode < 300 || statusCode > 399)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Redirect status code must be 3xx (300–399).");
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host.State() == SipDialogState.Terminated) return;
            if (!_host.IsInbound() || _host.State() != SipDialogState.Ringing)
                throw new InvalidOperationException(
                    $"RedirectAsync is only valid for inbound dialogs in Ringing state; current state is {_host.State()}.");
            var invite = _host.InitialInvite();
            var localTag = _host.LocalTag();
            if (invite is null || string.IsNullOrWhiteSpace(localTag))
                throw new InvalidOperationException("Inbound INVITE context is missing.");
            // RFC 3261 §8.3: Build 3xx response from inbound INVITE.
            // Record-Route MUST NOT be forwarded in a redirect response (§8.3).
            // Contact header carries the redirect targets, NOT the local contact.
            var redirectHeaders = _headerService.CreateResponseHeadersFromRequest(
                invite,
                localTag,
                includeContentType: false);
            redirectHeaders.Remove("Record-Route");
            redirectHeaders["Contact"] = string.Join(", ",
                contactUris.Select(u => u.Contains('<') ? u : $"<{u}>"));
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
                    invite,
                    _host.RemoteEndPoint(),
                    _config.SignalingTransport,
                    statusCode,
                    reasonPhrase,
                    redirectHeaders,
                    body: null,
                    ct)
                .ConfigureAwait(false);
            _host.TransitionTo(
                SipDialogState.Terminated,
                SipReasonHeader.CreateSipStatusReason(statusCode, reasonPhrase));
        }
        finally
        {
            _host.ReleaseOperationGate();
        }
    }
}
