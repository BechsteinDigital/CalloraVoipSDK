using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>
/// Payload for the call <c>IceConnectionStateChanged</c> event: a running change of the ICE transport
/// state after establishment (for example <see cref="CallIceState.Connected"/> →
/// <see cref="CallIceState.Disconnected"/> on RFC 7675 consent loss). Complements the one-shot
/// <see cref="ICall.IceSnapshot"/>, which only reports the final establishment state.
/// </summary>
public sealed class CallIceConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>The ICE state the leg transitioned from.</summary>
    public CallIceState OldState { get; }

    /// <summary>The ICE state the leg transitioned to.</summary>
    public CallIceState NewState { get; }

    /// <summary>The call whose ICE transport state changed.</summary>
    public ICall Call { get; }

    internal CallIceConnectionStateChangedEventArgs(CallIceState old, CallIceState next, ICall call)
        => (OldState, NewState, Call) = (old, next, call);
}
