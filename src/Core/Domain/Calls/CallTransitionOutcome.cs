namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// What a state-changing call action's commit did (#165 P2-4). An action checks its precondition, awaits a
/// signaling round-trip, and only then commits — so by commit time the call may already have moved.
/// </summary>
internal enum CallTransitionOutcome
{
    /// <summary>The action moved the call to the target state.</summary>
    Committed,

    /// <summary>
    /// The call was already in the target state: the signaling callback reported the transition before the
    /// action's own await returned. Ordinary — a peer that answers quickly gets here — and a success for the
    /// caller, who asked for exactly this state.
    /// </summary>
    AlreadyInTargetState,

    /// <summary>
    /// The call is in neither the state the action checked nor the one it wanted — something else (a remote
    /// BYE, another action) moved it while this one was in flight. The action must not commit, must not raise
    /// its follow-up event, and must not report success.
    /// </summary>
    Overtaken,
}
