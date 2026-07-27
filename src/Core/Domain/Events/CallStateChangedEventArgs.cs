using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for the call <c>StateChanged</c> event.</summary>
public sealed class CallStateChangedEventArgs : EventArgs
{
    /// <summary>The state the call transitioned from.</summary>
    public CallState OldState { get; }

    /// <summary>The state the call transitioned to.</summary>
    public CallState NewState { get; }

    /// <summary>The call whose state changed.</summary>
    public ICall     Call     { get; }

    /// <summary>
    /// Why the call terminated, populated only on the transition to <see cref="CallState.Terminated"/>
    /// (protocol-neutral, covering both local and remote terminations); <see langword="null"/> for every
    /// other transition, and for a terminating transition whose cause could not be determined.
    /// </summary>
    public CallTerminationReason? TerminationReason { get; }

    internal CallStateChangedEventArgs(
        CallState old,
        CallState next,
        ICall call,
        CallTerminationReason? terminationReason = null)
        => (OldState, NewState, Call, TerminationReason) = (old, next, call, terminationReason);
}
