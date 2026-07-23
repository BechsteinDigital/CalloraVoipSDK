namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Core SIP call signaling service for INVITE-based dialog handling.
/// </summary>
internal interface ISipCallSignalingService : IDisposable
{
    /// <summary>
    /// Raised when a new inbound INVITE arrives and created session is in Ringing state.
    /// </summary>
    event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite;

    /// <summary>
    /// Raised for an inbound out-of-dialog SIP MESSAGE (RFC 3428) after it is answered 200 OK.
    /// </summary>
    event EventHandler<SipIncomingMessageEventArgs>? IncomingMessage;

    /// <summary>
    /// Raised when an outbound INVITE session has been created and is about to start.
    /// Fires before the INVITE transaction begins, allowing callers to subscribe to
    /// session events (e.g. StateChanged) before the first provisional response arrives.
    /// </summary>
    event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted;

    /// <summary>
    /// Starts an outbound INVITE flow and returns the established session.
    /// </summary>
    /// <param name="request">The outbound INVITE parameters.</param>
    /// <param name="onSessionCreated">
    /// Optional callback invoked with the freshly created session immediately after it is created and
    /// before the INVITE is sent, so callers can bind it to a media adapter and observe the early dialog
    /// (Ringing/183) live instead of only after the 200 OK (F011). A redirect (3xx) that creates a fresh
    /// session for a new target invokes the callback again with the replacement session.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ISipCallSession> InviteAsync(
        SipInviteRequest request,
        Action<ISipCallSession>? onSessionCreated = null,
        CancellationToken ct = default);

    /// <summary>
    /// Initiates an out-of-dialog SIP SUBSCRIBE and returns a handle for the active subscription (RFC 6665 §4.1).
    /// The subscription is automatically refreshed before expiry and terminated when the handle is disposed.
    /// </summary>
    Task<SipSubscriptionHandle> SubscribeAsync(
        SipSubscribeRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Sends an out-of-dialog SIP MESSAGE (RFC 3428) and returns the final response status code (2xx on success).
    /// </summary>
    Task<int> SendMessageAsync(SipMessageRequest request, CancellationToken ct = default);
}

