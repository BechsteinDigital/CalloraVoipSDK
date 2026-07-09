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
    Task AcceptAsync(CancellationToken ct = default);
    Task HangupAsync(CancellationToken ct = default);
    Task HoldAsync(CancellationToken ct = default);
    Task UnholdAsync(CancellationToken ct = default);
    Task SendDtmfAsync(DtmfTone tone, CancellationToken ct = default);
    Task BlindTransferAsync(string targetUri, CancellationToken ct = default);

    /// <summary>
    /// Attended transfer: transfer this call to the party in <paramref name="consultationCall"/>.
    /// Both calls must be Connected or OnHold.
    /// </summary>
    Task<bool> AttendedTransferAsync(ICall consultationCall, CancellationToken ct = default);

    /// <summary>
    /// Rejects an inbound ringing call with a 4xx/5xx/6xx SIP status.
    /// </summary>
    Task<CallActionResult> RejectAsync(
        int statusCode = 486,
        string? reasonPhrase = null,
        CancellationToken ct = default);

    /// <summary>
    /// Redirects an inbound ringing call with a 3xx SIP response and contact targets.
    /// </summary>
    Task<CallActionResult> RedirectAsync(
        IReadOnlyList<string> contactUris,
        int statusCode = 302,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP INFO.
    /// </summary>
    Task<CallActionResult> SendInfoAsync(
        string contentType,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP OPTIONS.
    /// </summary>
    Task<CallActionResult> SendOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP SUBSCRIBE.
    /// </summary>
    Task<CallActionResult> SendSubscribeAsync(
        string eventType,
        int expiresSeconds = 300,
        string? acceptHeader = null,
        string? body = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP NOTIFY.
    /// </summary>
    Task<CallActionResult> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType = null,
        string? body = null,
        CancellationToken ct = default);
}
