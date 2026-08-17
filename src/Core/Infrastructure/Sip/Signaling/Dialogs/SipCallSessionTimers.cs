using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// The RFC 4028 session-timer half of a <see cref="SipCallSession"/>: it owns the
/// <see cref="SipSessionTimerManager"/>, resolves a negotiated <c>Session-Expires</c> onto it, and answers
/// the manager's two callbacks — send an in-dialog UPDATE refresh, and terminate the dialog when the
/// negotiated interval elapses.
/// </summary>
/// <remarks>
/// Extracted from the session so that file stays under the 1000-line limit (R3, #285). The three methods
/// were already pure forwarding into <see cref="SipCallSessionUtilities"/> with the session's dependencies
/// captured per call; holding them here instead adds no coupling that did not exist, and gives the dialog
/// core room for its next change — three consecutive fixes had to negotiate an extraction first.
/// <para>
/// Construction order matters and is the reason this type owns the manager rather than receiving one: the
/// manager is built with the two callbacks below, so a collaborator that merely used a manager would need it
/// to exist first, and the manager would need the collaborator first.
/// </para>
/// </remarks>
internal sealed class SipCallSessionTimers : IDisposable
{
    private readonly SipSessionTimerManager _manager;

    /// <summary>
    /// Wires the timer manager to the session's operation gate, transaction service and state machine.
    /// </summary>
    /// <param name="operationGate">The session's operation semaphore; a refresh and a BYE both take it.</param>
    /// <param name="isDisposed">Whether the session has been disposed — checked before acting on a callback.</param>
    /// <param name="state">Reads the dialog's current state.</param>
    /// <param name="sendSessionRefreshUpdateAsync">Sends the in-dialog UPDATE that refreshes the session.</param>
    /// <param name="sendByeAsync">Sends the BYE that ends a session whose timer expired.</param>
    /// <param name="transitionTo">Commits the resulting dialog-state transition.</param>
    /// <param name="releaseOperationGate">Releases the operation gate, tolerating a racing disposal.</param>
    /// <param name="callId">The dialog's Call-ID, for logging.</param>
    /// <param name="logger">The session's logger.</param>
    public SipCallSessionTimers(
        SemaphoreSlim operationGate,
        Func<bool> isDisposed,
        Func<SipDialogState> state,
        Func<CancellationToken, Task<bool>> sendSessionRefreshUpdateAsync,
        Func<CancellationToken, Task> sendByeAsync,
        Action<SipDialogState, SipDialogTerminationReason?> transitionTo,
        Action releaseOperationGate,
        string callId,
        ILogger logger)
    {
        _manager = new SipSessionTimerManager(
            logger,
            ct => SipCallSessionUtilities.SendSessionRefreshAsync(
                operationGate,
                isDisposed,
                state,
                sendSessionRefreshUpdateAsync,
                releaseOperationGate,
                callId,
                logger,
                ct),
            ct => SipCallSessionUtilities.HandleSessionTimerExpiredAsync(
                operationGate,
                isDisposed,
                state,
                sendByeAsync,
                transitionTo,
                releaseOperationGate,
                callId,
                logger,
                ct));
    }

    /// <summary>
    /// Applies the negotiated session-timer values from a <c>Session-Expires</c> header (RFC 4028 §7.1).
    /// A header that carries no usable negotiation leaves the timer untouched.
    /// </summary>
    /// <param name="sessionExpiresHeader">The header value, or null when the peer sent none.</param>
    /// <param name="localIsRequester">Whether this side issued the request the header answers.</param>
    public void ApplyNegotiation(string? sessionExpiresHeader, bool localIsRequester)
    {
        if (!SipSessionTimerPolicy.TryResolveNegotiation(
                sessionExpiresHeader,
                localIsRequester,
                out var intervalSeconds,
                out var localIsRefresher))
        {
            return;
        }

        _manager.ApplyNegotiation(intervalSeconds, localIsRefresher);
    }

    /// <summary>Stops the timer — the dialog has reached a terminal state.</summary>
    public void Stop() => _manager.Stop();

    /// <inheritdoc />
    public void Dispose() => _manager.Dispose();
}
