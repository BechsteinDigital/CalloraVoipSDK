using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Publications;

namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>
/// A registered SIP line (account) that places and receives calls.
/// </summary>
/// <remarks>
/// All events on this interface are raised on the SDK's SIP signaling/registration thread and run
/// the handler synchronously on it. Handlers <b>must not block or throw</b> — a blocked handler
/// stalls signaling and registration for the line. Off-load real work to your own task; see
/// <see cref="ICall"/> remarks for the same contract and an example.
/// </remarks>
public interface IPhoneLine
{
    /// <summary>Stable identifier of this line within the SDK.</summary>
    LineId     LineId   { get; }

    /// <summary>The SIP account this line registers and calls with.</summary>
    SipAccount Account  { get; }

    /// <summary>Current registration state; changes are signalled by <see cref="StateChanged"/>.</summary>
    LineState  State    { get; }

    /// <summary>
    /// The addresses the registrar announced for this line — which numbers reach it (RFC 3455
    /// <c>P-Associated-URI</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only source that needs no operator and no call.</b> A trunk contract brings a list
    /// somebody can type in; a registration account brings nothing, and its username is as often
    /// <c>admin123</c> as a number. A registrar that sends this header answers the question outright,
    /// at registration time.
    /// </para>
    /// <para>
    /// The first entry is the default public identity — the one the network uses when a request does
    /// not say otherwise — so the order is meaningful and preserved. Both <c>sip:</c> and <c>tel:</c>
    /// occur, often for the same number, and they are returned as announced: what counts as a
    /// telephone number is a question about a dial plan, and this layer does not have one.
    /// </para>
    /// <para>
    /// Empty means <em>nobody said</em>, never <em>there are none</em>. Carrier registrars send it; a
    /// box on the local network generally does not, and reading the empty list as a statement would
    /// turn a silent registrar into a line with no numbers.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> AnnouncedAddresses => [];

    /// <summary>
    /// Subscribes to somebody else's state and keeps receiving it as it changes (RFC 6665).
    /// </summary>
    /// <param name="eventType">
    /// The event package: <c>dialog</c> for what a line is doing, <c>presence</c> for whether somebody
    /// is available, or anything else the far end offers.
    /// </param>
    /// <param name="targetUri">Whose state to watch, as a SIP URI.</param>
    /// <param name="expiresSeconds">Requested lifetime; the SDK refreshes before it runs out.</param>
    /// <param name="accept">
    /// Optional <c>Accept</c> header. Left out, the far end picks the document format it prefers — which
    /// is usually right and occasionally means receiving one this SDK does not parse.
    /// </param>
    /// <param name="ct">Cancels the initial SUBSCRIBE.</param>
    /// <returns>The live subscription. Dispose it to unsubscribe.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is how a telephone system shows its colleagues.</b> Subscribing to the <c>dialog</c>
    /// package of an extension and reading <see cref="Subscriptions.SipDialogInfo"/> out of each
    /// notification is a busy lamp: idle, ringing, on a call. Polling cannot do it — by the time the
    /// answer arrives the state has moved.
    /// </para>
    /// <para>
    /// Notifications carry the document unparsed as well.
    /// <see cref="Subscriptions.SipDialogInfo.TryParse"/> and
    /// <see cref="Subscriptions.SipPresence.TryParse"/> read the two this SDK understands; an event
    /// package it does not know still reaches the application intact rather than being dropped for not
    /// fitting a model.
    /// </para>
    /// </remarks>
    Task<Subscriptions.ISipSubscription> SubscribeAsync(
        string eventType,
        string targetUri,
        int expiresSeconds = 300,
        string? accept = null,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This line does not support subscriptions. Implemented by the SIP line channel; a default "
            + "is provided so adding this member does not break implementations outside this repository.");

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when the line's registration <see cref="State"/> changes (signaling thread).</summary>
    event EventHandler<LineStateChangedEventArgs>?    StateChanged;

    /// <summary>
    /// Raised when an inbound call arrives (signaling thread). The call is already ringing when this
    /// fires; accept or reject it via the call on the event args. Keep the handler fast (see remarks).
    /// </summary>
    event EventHandler<IncomingCallEventArgs>?         IncomingCall;

    /// <summary>
    /// Raised when an outbound call reaches Ringing (early dialog) before it is answered, giving the
    /// caller a handle to observe early media / call state while DialAsync still awaits the 200 OK.
    /// Runs synchronously on the signaling thread; keep the handler fast and non-blocking (see remarks).
    /// </summary>
    event EventHandler<OutboundCallRingingEventArgs>? OutboundCallRinging;

    /// <summary>
    /// Raised when an inbound out-of-dialog SIP MESSAGE (RFC 3428 pager-mode IM) arrives for this line
    /// (signaling thread). The SDK has already answered it 200 OK; no reply is required. Keep the handler
    /// fast (see remarks).
    /// </summary>
    event EventHandler<IncomingMessageEventArgs>?      IncomingMessage;

    /// <summary>
    /// Raised each time the SDK begins a reconnect attempt after losing the SIP registration.
    /// The line is already in <see cref="LineState.Reconnecting"/> when this event fires.
    /// </summary>
    event EventHandler<LineReconnectingEventArgs>?    LineReconnecting;

    /// <summary>
    /// Raised when the line permanently fails to re-register and enters
    /// <see cref="LineState.Failed"/>.  No further reconnect attempts will be made.
    /// </summary>
    event EventHandler<LineReconnectFailedEventArgs>? LineReconnectFailed;

    /// <summary>
    /// The reason for the last permanent registration/reconnect failure, or <see langword="null"/> if the
    /// line never failed. Captured as state before the <see cref="LineState.Failed"/> transition, so a
    /// consumer that only observes the terminal Failed state can read the cause race-free even if it missed
    /// the <see cref="LineReconnectFailed"/> event (e.g. a fast failure that fired before it subscribed).
    /// </summary>
    LineReconnectFailedEventArgs? LastReconnectFailure => null;

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Places an outbound call from this line to <paramref name="targetUri"/> and returns the new call
    /// already in <see cref="CallState.Dialing"/>. Track progress via the returned call's
    /// <see cref="ICall.StateChanged"/> event.
    /// </summary>
    /// <param name="targetUri">Destination SIP URI or number to dial.</param>
    /// <param name="options">Per-call options; <see langword="null"/> uses <see cref="DialOptions.Default"/>.</param>
    /// <param name="ct">Cancels the dial attempt.</param>
    /// <returns>
    /// The newly created outbound call. When the remote end rejects the INVITE with a SIP final response
    /// (486 Busy, 408/480 no-answer, 603 decline, …), the returned call is already in
    /// <see cref="CallState.Terminated"/> and its <see cref="ICall.TerminationReason"/> classifies the
    /// outcome — no exception is thrown for a signaled rejection. A transport/network fault that never
    /// produced a call outcome still propagates as an exception.
    /// </returns>
    Task<ICall> DialAsync(string targetUri, DialOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Sends an out-of-dialog SIP MESSAGE (RFC 3428 pager-mode instant message) from this line.
    /// </summary>
    /// <param name="targetUri">The recipient's SIP URI.</param>
    /// <param name="body">The message text/body.</param>
    /// <param name="contentType">The body's MIME type; defaults to <c>text/plain</c>.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>A task that completes when the peer answers 2xx; it faults on a non-2xx or no response.</returns>
    Task SendMessageAsync(string targetUri, string body, string contentType = "text/plain", CancellationToken ct = default);

    /// <summary>
    /// Publishes event state for this line's address-of-record via SIP PUBLISH (RFC 3903 event state
    /// publication), for example presence. Returns the SIP-ETag and granted lifetime for a later refresh.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="body">The event-state document to publish (for example a PIDF body).</param>
    /// <param name="contentType">The body's MIME type (e.g. <c>application/pidf+xml</c>).</param>
    /// <param name="expiresSeconds">Requested publication lifetime in seconds. Defaults to 3600.</param>
    /// <param name="ct">Cancels the publish.</param>
    /// <returns>The assigned entity-tag and granted lifetime; faults on a non-2xx or no response.</returns>
    Task<PublishResult> PublishAsync(string eventType, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default);

    /// <summary>
    /// Refreshes a prior publication's lifetime via SIP-If-Match (RFC 3903), retaining the existing event state.
    /// The <paramref name="etag"/> is the SIP-ETag from a prior PublishAsync/refresh.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to refresh.</param>
    /// <param name="expiresSeconds">Requested publication lifetime in seconds. Defaults to 3600.</param>
    /// <param name="ct">Cancels the refresh.</param>
    /// <returns>The assigned entity-tag and granted lifetime; faults on a non-2xx or no response.</returns>
    Task<PublishResult> RefreshPublicationAsync(string eventType, string etag, int expiresSeconds = 3600, CancellationToken ct = default);

    /// <summary>
    /// Modifies a prior publication by replacing its body via SIP-If-Match (RFC 3903). The
    /// <paramref name="etag"/> is the SIP-ETag from a prior PublishAsync/refresh.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to modify.</param>
    /// <param name="body">The replacement event-state document to publish (for example a PIDF body).</param>
    /// <param name="contentType">The body's MIME type; defaults to <c>text/plain</c>.</param>
    /// <param name="expiresSeconds">Requested publication lifetime in seconds. Defaults to 3600.</param>
    /// <param name="ct">Cancels the modify.</param>
    /// <returns>The assigned entity-tag and granted lifetime; faults on a non-2xx or no response.</returns>
    Task<PublishResult> ModifyPublicationAsync(string eventType, string etag, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default);

    /// <summary>
    /// Removes a prior publication via SIP-If-Match with Expires: 0 (RFC 3903). The <paramref name="etag"/>
    /// is the SIP-ETag from a prior PublishAsync/refresh.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to remove.</param>
    /// <param name="ct">Cancels the remove.</param>
    /// <returns>A task that completes when the peer answers 2xx; it faults on a non-2xx or no response.</returns>
    Task RemovePublicationAsync(string eventType, string etag, CancellationToken ct = default);

    /// <summary>
    /// Unregisters this line (sends REGISTER with Expires: 0) and stops automatic re-registration.
    /// </summary>
    /// <param name="ct">Cancels the unregister request.</param>
    Task UnregisterAsync(CancellationToken ct = default);
}
