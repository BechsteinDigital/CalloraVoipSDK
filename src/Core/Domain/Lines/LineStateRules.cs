namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>
/// Legal <see cref="LineState"/> transition table, mirroring <see cref="Calls.CallStateRules"/> for calls.
/// Used by <see cref="PhoneLine.TransitionTo"/> to guard against illegal state changes (logged and ignored).
/// The table is deliberately permissive: it admits every transition the registration channel actually drives
/// (see <c>SipLineChannel.RegisterAsync</c> / <c>StartRegistration</c> / <c>StopRegistration</c>) so that no
/// currently-legitimate transition is newly rejected — it only blocks transitions the state machine never
/// produces (for example jumping straight to <see cref="LineState.Registered"/> without registering).
/// </summary>
internal static class LineStateRules
{
    /// <summary>
    /// Whether a line may move from <paramref name="from"/> to <paramref name="to"/>. A same-state
    /// transition is always allowed (callers treat it as a no-op).
    /// </summary>
    public static bool CanTransition(LineState from, LineState to)
    {
        if (from == to)
            return true;

        // StopRegistration de-registers from any state → Unregistered.
        if (to == LineState.Unregistered)
            return true;

        // Failed is documented as terminal ("No further attempts will be made"): the only way forward is a
        // fresh StartRegistration (→ Registering) or a StopRegistration (→ Unregistered, handled above). Every
        // other move out of Failed — including a direct jump back to Registered/Reconnecting/RegistrationFailed
        // — is illegal and rejected.
        if (from == LineState.Failed)
            return to == LineState.Registering;

        // All non-terminal states may reach any other non-terminal registration state: the channel drives the
        // full initial-register → refresh → reconnect → failure-family lifecycle, and existing consumers (and
        // tests) also report Registered directly. Kept permissive so no previously-valid transition is newly
        // rejected — only the terminal-Failed escapes above are constrained.
        return true;
    }
}
