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
    /// <summary>Stable identifier of this call within the SDK.</summary>
    CallId        CallId      { get; }

    /// <summary>Current lifecycle state; changes are signalled by <see cref="StateChanged"/>.</summary>
    CallState     State       { get; }

    /// <summary>Whether the call was placed locally (<see cref="CallDirection.Outbound"/>) or received (<see cref="CallDirection.Inbound"/>).</summary>
    CallDirection Direction   { get; }

    /// <summary>The remote party's SIP URI or address-of-record.</summary>
    string        RemoteParty { get; }

    /// <summary>UTC timestamp of when this call object was created (dial or inbound INVITE).</summary>
    DateTimeOffset StartedAt  { get; }

    /// <summary>The phone line this call belongs to.</summary>
    IPhoneLine Line { get; }

    /// <summary>
    /// Why this call terminated, or <see langword="null"/> until it reaches
    /// <see cref="CallState.Terminated"/> (and null when the cause could not be determined). The value
    /// is protocol-neutral — it classifies both local and remote terminations (busy, no-answer, reject,
    /// normal completion) — and is already populated when the
    /// <see cref="CallStateChangedEventArgs.NewState"/> of <see cref="StateChanged"/> is
    /// <see cref="CallState.Terminated"/>, so a handler can read it there. The same reason is also carried
    /// on that event's <see cref="CallStateChangedEventArgs.TerminationReason"/>.
    /// </summary>
    CallTerminationReason? TerminationReason { get; }

    /// <summary>
    /// Negotiated media parameters (codec, endpoints). Set once
    /// <see cref="CallState.Connected"/> is reached; null before that.
    /// </summary>
    CallMediaParameters? MediaParameters { get; }

    /// <summary>
    /// SDP body carried on a provisional (180/183) response — the early-media description offered by
    /// the far end before the call is answered (RFC 3960), separate from the final answer in
    /// <see cref="MediaParameters"/>. <see langword="null"/> until such a provisional arrives.
    /// Default implementation returns null.
    /// </summary>
    string? EarlyMediaSdp => null;

    /// <summary>
    /// Latest quality snapshot derived from RTP/RTCP runtime metrics.
    /// Before media starts, this value is an empty baseline snapshot.
    /// </summary>
    CallQualitySnapshot QualitySnapshot { get; }

    /// <summary>
    /// Latest raw RTP transport statistics (SSRC identifiers, packet/octet counters, RFC 3550
    /// loss and jitter counters) for this leg, or <see langword="null"/> until the first RTCP
    /// reporting interval has produced counters. Complements <see cref="QualitySnapshot"/> with the
    /// underlying uninterpreted counters for diagnostics and billing.
    /// </summary>
    CallRtpStatistics? RtpStatistics { get; }

    /// <summary>
    /// Read-only ICE (RFC 8445) connectivity snapshot for this leg once candidate-pair selection
    /// completes, or <see langword="null"/> for calls where ICE was not negotiated. Reports the
    /// final ICE state and the selected local/remote candidate pair.
    /// </summary>
    CallIceSnapshot? IceSnapshot { get; }

    /// <summary>
    /// Running ICE transport state for this leg (RFC 8445 / RFC 7675). Unlike the one-shot
    /// <see cref="IceSnapshot"/> (final establishment state), this tracks post-establishment changes —
    /// notably <see cref="CallIceState.Connected"/> → <see cref="CallIceState.Disconnected"/> when
    /// consent is lost. <see cref="CallIceState.Disabled"/> for non-ICE legs. Changes are signalled by
    /// <see cref="IceConnectionStateChanged"/>.
    /// </summary>
    CallIceState IceConnectionState { get; }

    /// <summary>
    /// Peer-asserted caller identity (P-Asserted-Identity, RFC 3325) parsed from an inbound INVITE
    /// when the sending peer is trusted, for trunk/PBX routing; <see langword="null"/> for outbound
    /// calls, untrusted peers, or when the header is absent.
    /// </summary>
    string? RemoteAssertedIdentity { get; }

    /// <summary>
    /// First <c>Diversion</c> URI (RFC 5806) from an inbound INVITE describing where the call was
    /// diverted from; <see langword="null"/> for outbound calls or when no Diversion header is present.
    /// Informational routing history, surfaced as-received.
    /// </summary>
    string? Diversion { get; }

    /// <summary>
    /// Every address this call was forwarded from, oldest first — the number the caller originally
    /// dialled at the front, the party that forwarded it to us at the back. Empty for outbound calls
    /// and whenever no retargeting was reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from whichever header the carrier sent: <c>History-Info</c> (RFC 4244) or
    /// <c>Diversion</c> (RFC 5806). The two are ordered opposite ways and no carrier sends both
    /// consistently, so a consumer reading one of them directly is correct with part of the market
    /// and silently blind with the rest — and blind here means a forwarded call is indistinguishable
    /// from a direct one. Both are normalised to this one order.
    /// </para>
    /// <para>
    /// An empty list means nothing was <em>reported</em>, not that the call arrived directly. A
    /// carrier that sends neither header leaves the distinction unavailable.
    /// </para>
    /// <para>
    /// <see cref="Diversion"/> stays what it always was — the first URI of the first Diversion header
    /// row — for consumers that want exactly that.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> DiversionChain => Array.Empty<string>();

    /// <summary>
    /// The remote party's display name as received — the caller's name from the inbound INVITE
    /// <c>From</c> header (RFC 3261 §8.1.1.3). <see langword="null"/> when the header carried no
    /// display name, or for outbound calls. Complements <see cref="RemoteParty"/> (which is the URI
    /// without the display name). Defaults to <see langword="null"/>.
    /// </summary>
    string? RemoteDisplayName => null;

    /// <summary>
    /// The remote party's number — the user part of <see cref="RemoteParty"/> (the caller's number on
    /// an inbound call), parsed by the SDK so every consumer shares one implementation.
    /// <see langword="null"/> when the URI has no user part. Defaults to <see langword="null"/>.
    /// </summary>
    string? RemoteNumber => null;

    /// <summary>
    /// The local party's SIP URI in this dialog: on an inbound call the address the call was placed to
    /// — the <c>To</c>/Request URI, i.e. the dialed number (DID) for a SIP trunk; on an outbound call
    /// the local account address. Parallel to <see cref="RemoteParty"/>. Defaults to <see langword="null"/>.
    /// </summary>
    string? LocalParty => null;

    /// <summary>
    /// The dialed number (DID) an inbound call was addressed to — the user part of
    /// <see cref="LocalParty"/> (the <c>To</c>/Request URI). This is the number that selected the
    /// receiving line for a SIP trunk. <see langword="null"/> for outbound calls or when the URI has
    /// no user part. Defaults to <see langword="null"/>.
    /// </summary>
    string? CalledNumber => null;

    /// <summary>
    /// Whether this call's outgoing audio is currently muted locally (see <see cref="MuteAsync"/>).
    /// <see langword="false"/> by default.
    /// </summary>
    bool IsMuted => false;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when <see cref="State"/> changes. Serialized on the SIP signaling thread. Changes are
    /// <b>not</b> buffered: a handler attached after a transition does not receive that earlier
    /// transition. Read <see cref="State"/> for the current value, and subscribe before initiating an
    /// action to observe every subsequent change. See the interface remarks for the handler contract.
    /// </summary>
    event EventHandler<CallStateChangedEventArgs>?  StateChanged;

    /// <summary>
    /// Raised when the remote party puts the call on or off hold, and only when the hold state
    /// actually changes (a duplicate or pre-<see cref="CallState.Connected"/> hold indication does not
    /// re-raise it). Serialized on the SIP signaling thread. Like <see cref="StateChanged"/>, changes
    /// are not buffered for handlers attached later.
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

    /// <summary>
    /// Raised when the running ICE transport state changes after establishment (for example
    /// <see cref="CallIceState.Connected"/> → <see cref="CallIceState.Disconnected"/> on consent loss).
    /// Fires on a media/RTCP thread, not the signaling thread. Lets the application react to a lost media
    /// path — tear down, alert the user, or (later) trigger an ICE restart. Not buffered.
    /// </summary>
    event EventHandler<CallIceConnectionStateChangedEventArgs>? IceConnectionStateChanged;

    /// <summary>
    /// Raised when inbound media goes silent on a connected call, and again when it resumes (#261, ADR-069).
    /// Fires on a media/RTCP thread, not the signaling thread. Not buffered.
    /// </summary>
    /// <remarks>
    /// This is a notification, not a teardown: it fires while the peer is still demonstrably alive (RTCP keeps
    /// arriving), which is the normal state during silence suppression (RFC 3389), hold, and the bridge switch
    /// of a transfer. The application decides what silence means for its use case — play a prompt, escalate,
    /// end the call — long before the SDK's own liveness timeout would. A peer that stops sending
    /// <em>everything</em> is a separate matter and ends the call with a
    /// <see cref="CallTerminationReason"/> that says so.
    /// </remarks>
    event EventHandler<CallMediaFlowChangedEventArgs>? MediaFlowChanged;

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
    /// Mutes or unmutes this call's <b>outgoing</b> audio locally: the SDK stops (or resumes) sending
    /// this call's captured audio to the peer. Unlike <see cref="HoldAsync"/> it is <b>not signalled</b>
    /// to the peer (no re-INVITE), and unlike the client-wide device mute
    /// (<c>IVoipClient.SetAudioInputMuted</c>) it affects <b>only this call</b>, so on a client with
    /// several concurrent calls each mutes independently. While muted the peer receives no audio for this
    /// call (no packets are sent for the outgoing direction); inbound audio is unaffected. Local-only, so
    /// it is valid in any live state and does not throw for state; a no-op if already in the requested
    /// state. Read the current state via <see cref="IsMuted"/>. The default implementation is a no-op.
    /// </summary>
    /// <param name="muted"><see langword="true"/> to mute outgoing audio; <see langword="false"/> to resume.</param>
    /// <param name="ct">Cancels the operation; on cancellation <see cref="OperationCanceledException"/> is thrown.</param>
    Task MuteAsync(bool muted, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Initiates a local ICE restart (RFC 8445 §9) on a connected, ICE-negotiated call: the SDK
    /// re-gathers on the existing media socket with fresh ICE credentials — a new ice-ufrag <b>and</b>
    /// ice-pwd — and re-offers them in a direction-preserving re-INVITE. The ICE role is preserved and
    /// media keeps flowing on the previously validated candidate pair until the new connectivity checks
    /// nominate a pair. Use this to recover a media path after a network change or a consent loss
    /// surfaced by <see cref="IceConnectionStateChanged"/>.
    /// </summary>
    /// <param name="ct">
    /// Accepted for signature symmetry but currently not forwarded to the transport, so the restart is
    /// not cancelled via this token.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The call state is not <see cref="CallState.Connected"/>, or ICE was not negotiated on this call.
    /// </exception>
    Task RestartIceAsync(CancellationToken ct = default);

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
