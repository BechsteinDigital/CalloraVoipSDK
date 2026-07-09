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
    LineId     LineId   { get; }
    SipAccount Account  { get; }
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
    Task<ICall> DialAsync(string targetUri, DialOptions? options = null, CancellationToken ct = default);
    Task UnregisterAsync(CancellationToken ct = default);
}
