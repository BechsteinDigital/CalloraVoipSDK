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

    internal CallStateChangedEventArgs(CallState old, CallState next, ICall call)
        => (OldState, NewState, Call) = (old, next, call);
}
