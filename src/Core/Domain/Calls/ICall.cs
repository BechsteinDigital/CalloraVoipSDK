using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// A single call and its lifecycle.
/// </summary>
/// <remarks>
/// <para><b>Event threading contract.</b> Event handlers run <em>synchronously on the SDK thread
/// that raised the event</em> — this is usually not the thread that created the call. Handlers
/// therefore <b>must not block or perform long-running/synchronous I/O</b>: a blocked handler stalls
/// the SDK path that raised it (SIP signaling or media), delaying every other call on the same line.
/// Off-load real work to your own task or queue, e.g.
/// <c>call.StateChanged += (_, e) =&gt; _ = Task.Run(() =&gt; Handle(e));</c>. Handlers should also not
/// throw. See each event for the specific thread and whether it is serialized.</para>
/// <para><see cref="TransferRequested"/> is the exception to off-loading: it needs a synchronous
/// accept/reject decision, so make that decision quickly inline (do not await long work).</para>
/// <para><b>Error contract.</b> The action methods split into two error styles by return type.
/// The core lifecycle operations return <see cref="System.Threading.Tasks.Task"/> (or
/// <see cref="System.Threading.Tasks.Task{TResult}"/> of <see cref="bool"/> for
/// <see cref="AttendedTransferAsync"/>) and <b>throw</b>: invalid usage surfaces as
/// <see cref="System.InvalidOperationException"/> (wrong state or direction),
/// <see cref="System.ArgumentException"/> (invalid argument), and cancellation surfaces as
/// <see cref="System.OperationCanceledException"/>; transport, timeout and unexpected transport-layer
/// failures propagate as-is. The extended in-dialog / inbound-response operations return
/// <see cref="CallActionResult"/> instead and <b>do not throw for foreseeable outcomes</b>: an
/// invalid state or direction, a protocol rejection (for example a SIP 4xx from the peer), an invalid
/// request, cancellation, and transport failures are all reported as a non-success
/// <see cref="CallActionResult"/> whose <see cref="CallActionResult.Status"/> classifies the cause
/// (see <see cref="CallActionStatus"/>). These result-returning methods are the ones that involve a
/// SIP round-trip whose negative answer is a normal protocol result rather than a programming error.
/// Per-method tags below state the exact exceptions and result semantics.</para>
/// </remarks>
public interface ICall
{
    CallId        CallId      { get; }
    CallState     State       { get; }
    CallDirection Direction   { get; }
    string        RemoteParty { get; }
    DateTimeOffset StartedAt  { get; }

    /// <summary>The phone line this call belongs to.</summary>
    IPhoneLine Line { get; }

    /// <summary>
    /// Negotiated media parameters (codec, endpoints). Set once
    /// <see cref="CallState.Connected"/> is reached; null before that.
    /// </summary>
    CallMediaParameters? MediaParameters { get; }

    /// <summary>
    /// Latest quality snapshot derived from RTP/RTCP runtime metrics.
    /// Before media starts, this value is an empty baseline snapshot.
    /// </summary>
    CallQualitySnapshot QualitySnapshot { get; }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when <see cref="State"/> changes. Serialized on the SIP signaling thread. If no
    /// handler is attached yet, state changes are buffered and replayed in order once one is —
    /// the initial state is never missed. See the interface remarks for the handler contract.
    /// </summary>
    event EventHandler<CallStateChangedEventArgs>?  StateChanged;

    /// <summary>
    /// Raised when the remote party puts the call on or off hold. Serialized on the SIP signaling
    /// thread; buffered and replayed like <see cref="StateChanged"/> when no handler is attached.
    /// </summary>
    event EventHandler<HoldStateChangedEventArgs>?  HoldStateChanged;

    /// <summary>
    /// Raised when a DTMF tone is received. This event may fire from <em>two</em> threads: the SIP
    /// signaling thread (SIP INFO tones) and the media receive thread (RFC 4733 in-band RTP events).
    /// It is therefore not guaranteed single-threaded — keep the handler thread-safe and fast.
    /// </summary>
    event EventHandler<DtmfReceivedEventArgs>?      DtmfReceived;

    /// <summary>
    /// Raised when the peer requests a transfer (SIP REFER), on the SIP signaling thread. The
    /// handler must set the accept/reject decision on the event args synchronously (see remarks);
    /// it drives the SIP response, so decide quickly and do not await long-running work.
    /// </summary>
    event EventHandler<TransferRequestedEventArgs>? TransferRequested;

    /// <summary>
    /// Raised periodically with an updated call-quality snapshot. Fires on a media/RTCP thread
    /// (RTCP send-timer and receive driven, not the signaling thread), so it may interleave with
    /// the signaling-thread events above. Not buffered.
    /// </summary>
    event EventHandler<CallQualitySnapshotChangedEventArgs>? QualitySnapshotChanged;

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts an inbound ringing call and moves it to <see cref="CallState.Connected"/>.
    /// </summary>
    /// <param name="ct">Cancels the accept; on cancellation <see cref="OperationCanceledException"/> is thrown.</param>
    /// <exception cref="InvalidOperationException">
    /// The call is not <see cref="CallDirection.Inbound"/>, or its state is not
    /// <see cref="CallState.Ringing"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    Task AcceptAsync(CancellationToken ct = default);

    /// <summary>
    /// Hangs up the call and transitions it to <see cref="CallState.Terminated"/>. If the call is
    /// already <see cref="CallState.Terminated"/> this is a no-op that completes successfully.
    /// </summary>
    /// <param name="ct">
    /// Accepted for signature symmetry but currently not forwarded to the transport, so the hangup is
    /// not cancelled via this token.
    /// </param>
    Task HangupAsync(CancellationToken ct = default);

    /// <summary>
    /// Places the active call on hold and emits a local <see cref="HoldStateChanged"/> event.
    /// </summary>
    /// <param name="ct">
    /// Accepted for signature symmetry but currently not forwarded to the transport, so the hold is
    /// not cancelled via this token.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The call state is not <see cref="CallState.Connected"/>.
    /// </exception>
    Task HoldAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes the call off hold and emits a local <see cref="HoldStateChanged"/> event.
    /// </summary>
    /// <param name="ct">
    /// Accepted for signature symmetry but currently not forwarded to the transport, so the unhold is
    /// not cancelled via this token.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The call state is not <see cref="CallState.OnHold"/>.
    /// </exception>
    Task UnholdAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends one DTMF tone while the call is connected.
    /// </summary>
    /// <param name="tone">The tone to send.</param>
    /// <param name="ct">
    /// Accepted for signature symmetry but currently not forwarded to the transport, so the send is
    /// not cancelled via this token.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The call state is not <see cref="CallState.Connected"/>.
    /// </exception>
    Task SendDtmfAsync(DtmfTone tone, CancellationToken ct = default);

    /// <summary>
    /// Performs a blind transfer to <paramref name="targetUri"/>. On a successful transfer the call
    /// moves to <see cref="CallState.Terminated"/>; if the transfer fails it returns to
    /// <see cref="CallState.Connected"/>. Either way the task completes without a return value.
    /// </summary>
    /// <param name="targetUri">The transfer target URI.</param>
    /// <param name="ct">Cancels the transfer; on cancellation <see cref="OperationCanceledException"/> is thrown.</param>
    /// <exception cref="InvalidOperationException">
    /// The call state is not <see cref="CallState.Connected"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    Task BlindTransferAsync(string targetUri, CancellationToken ct = default);

    /// <summary>
    /// Attended transfer: transfer this call to the party in <paramref name="consultationCall"/>.
    /// </summary>
    /// <param name="consultationCall">
    /// The consultation call to transfer to. Must be a call created by this SDK.
    /// </param>
    /// <param name="ct">Cancels the transfer; on cancellation <see cref="OperationCanceledException"/> is thrown.</param>
    /// <returns>
    /// <see langword="true"/> when the transfer completed and this call moved to
    /// <see cref="CallState.Terminated"/>; <see langword="false"/> when the transfer did not complete
    /// and this call returned to <see cref="CallState.Connected"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="consultationCall"/> is not a call created by this SDK.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    Task<bool> AttendedTransferAsync(ICall consultationCall, CancellationToken ct = default);

    /// <summary>
    /// Rejects an inbound ringing call with a 4xx/5xx/6xx SIP status. Does not throw for foreseeable
    /// outcomes; the result carries the classification instead (see the interface error contract).
    /// </summary>
    /// <param name="statusCode">The SIP status code to reject with (default 486 Busy Here).</param>
    /// <param name="reasonPhrase">Optional SIP reason phrase; a default is derived when omitted.</param>
    /// <param name="ct">Cancels the operation; reported as <see cref="CallActionStatus.Canceled"/>, not thrown.</param>
    /// <returns>
    /// <see cref="CallActionStatus.Succeeded"/> once the reject is sent and the call is terminated;
    /// <see cref="CallActionStatus.InvalidState"/> when the call is not
    /// <see cref="CallDirection.Inbound"/> or its state is not <see cref="CallState.Ringing"/> (checked
    /// before any SIP round-trip); <see cref="CallActionStatus.Canceled"/> on cancellation; or
    /// <see cref="CallActionStatus.Failed"/> on a transport-layer failure.
    /// </returns>
    Task<CallActionResult> RejectAsync(
        int statusCode = 486,
        string? reasonPhrase = null,
        CancellationToken ct = default);

    /// <summary>
    /// Redirects an inbound ringing call with a 3xx SIP response and contact targets. Does not throw
    /// for foreseeable outcomes; the result carries the classification instead.
    /// </summary>
    /// <param name="contactUris">The redirect contact target URIs.</param>
    /// <param name="statusCode">The 3xx SIP status code to respond with (default 302 Moved Temporarily).</param>
    /// <param name="ct">Cancels the operation; reported as <see cref="CallActionStatus.Canceled"/>, not thrown.</param>
    /// <returns>
    /// <see cref="CallActionStatus.Succeeded"/> once the redirect is sent and the call is terminated;
    /// <see cref="CallActionStatus.InvalidState"/> when the call is not
    /// <see cref="CallDirection.Inbound"/> or its state is not <see cref="CallState.Ringing"/> (checked
    /// before any SIP round-trip); <see cref="CallActionStatus.Canceled"/> on cancellation; or
    /// <see cref="CallActionStatus.Failed"/> on a transport-layer failure.
    /// </returns>
    Task<CallActionResult> RedirectAsync(
        IReadOnlyList<string> contactUris,
        int statusCode = 302,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP INFO. Does not throw for foreseeable outcomes; the result carries the
    /// classification instead.
    /// </summary>
    /// <param name="contentType">The INFO body content type.</param>
    /// <param name="body">The INFO message body.</param>
    /// <param name="ct">Cancels the operation; reported as <see cref="CallActionStatus.Canceled"/>, not thrown.</param>
    /// <returns>
    /// <see cref="CallActionStatus.Succeeded"/> once the INFO is sent;
    /// <see cref="CallActionStatus.Canceled"/> on cancellation;
    /// <see cref="CallActionStatus.InvalidRequest"/> for an invalid argument;
    /// <see cref="CallActionStatus.InvalidState"/> when the transport reports the operation invalid for
    /// the current state; or <see cref="CallActionStatus.Failed"/> on a transport-layer failure.
    /// </returns>
    Task<CallActionResult> SendInfoAsync(
        string contentType,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP OPTIONS. Does not throw for foreseeable outcomes; the result carries the
    /// classification instead.
    /// </summary>
    /// <param name="ct">Cancels the operation; reported as <see cref="CallActionStatus.Canceled"/>, not thrown.</param>
    /// <returns>
    /// <see cref="CallActionStatus.Succeeded"/> when the peer accepts;
    /// <see cref="CallActionStatus.Rejected"/> when the peer declines the OPTIONS;
    /// <see cref="CallActionStatus.Canceled"/> on cancellation; or
    /// <see cref="CallActionStatus.Failed"/> on a transport-layer failure.
    /// </returns>
    Task<CallActionResult> SendOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP SUBSCRIBE. Does not throw for foreseeable outcomes; the result carries the
    /// classification instead.
    /// </summary>
    /// <param name="eventType">The SIP event package to subscribe to.</param>
    /// <param name="expiresSeconds">Requested subscription duration in seconds (default 300).</param>
    /// <param name="acceptHeader">Optional Accept header value.</param>
    /// <param name="body">Optional request body.</param>
    /// <param name="ct">Cancels the operation; reported as <see cref="CallActionStatus.Canceled"/>, not thrown.</param>
    /// <returns>
    /// <see cref="CallActionStatus.Succeeded"/> when the peer accepts;
    /// <see cref="CallActionStatus.Rejected"/> when the peer declines the SUBSCRIBE;
    /// <see cref="CallActionStatus.Canceled"/> on cancellation; or
    /// <see cref="CallActionStatus.Failed"/> on a transport-layer failure.
    /// </returns>
    Task<CallActionResult> SendSubscribeAsync(
        string eventType,
        int expiresSeconds = 300,
        string? acceptHeader = null,
        string? body = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP NOTIFY. Does not throw for foreseeable outcomes; the result carries the
    /// classification instead.
    /// </summary>
    /// <param name="eventType">The SIP event package the notification belongs to.</param>
    /// <param name="subscriptionState">The Subscription-State header value.</param>
    /// <param name="contentType">Optional body content type.</param>
    /// <param name="body">Optional request body.</param>
    /// <param name="ct">Cancels the operation; reported as <see cref="CallActionStatus.Canceled"/>, not thrown.</param>
    /// <returns>
    /// <see cref="CallActionStatus.Succeeded"/> when the peer accepts;
    /// <see cref="CallActionStatus.Rejected"/> when the peer declines the NOTIFY;
    /// <see cref="CallActionStatus.Canceled"/> on cancellation; or
    /// <see cref="CallActionStatus.Failed"/> on a transport-layer failure.
    /// </returns>
    Task<CallActionResult> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType = null,
        string? body = null,
        CancellationToken ct = default);
}
