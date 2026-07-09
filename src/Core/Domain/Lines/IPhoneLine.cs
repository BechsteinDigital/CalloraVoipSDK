using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk.Core.Domain.Lines;

public interface IPhoneLine
{
    LineId     LineId   { get; }
    SipAccount Account  { get; }
    LineState  State    { get; }

    // ── Events ────────────────────────────────────────────────────────────────
    event EventHandler<LineStateChangedEventArgs>?    StateChanged;
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
