using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

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
    /// Raised each time the SDK begins a reconnect attempt after losing the SIP registration.
    /// The line is already in <see cref="LineState.Reconnecting"/> when this event fires.
    /// </summary>
    event EventHandler<LineReconnectingEventArgs>?    LineReconnecting;

    /// <summary>
    /// Raised when the line permanently fails to re-register and enters
    /// <see cref="LineState.Failed"/>.  No further reconnect attempts will be made.
    /// </summary>
    event EventHandler<LineReconnectFailedEventArgs>? LineReconnectFailed;

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Places an outbound call from this line to <paramref name="targetUri"/> and returns the new call
    /// already in <see cref="CallState.Dialing"/>. Track progress via the returned call's
    /// <see cref="ICall.StateChanged"/> event.
    /// </summary>
    /// <param name="targetUri">Destination SIP URI or number to dial.</param>
    /// <param name="options">Per-call options; <see langword="null"/> uses <see cref="DialOptions.Default"/>.</param>
    /// <param name="ct">Cancels the dial attempt.</param>
    /// <returns>The newly created outbound call.</returns>
    Task<ICall> DialAsync(string targetUri, DialOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Unregisters this line (sends REGISTER with Expires: 0) and stops automatic re-registration.
    /// </summary>
    /// <param name="ct">Cancels the unregister request.</param>
    Task UnregisterAsync(CancellationToken ct = default);
}
