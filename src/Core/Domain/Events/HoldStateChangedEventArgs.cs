using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for the call <c>HoldStateChanged</c> event.</summary>
public sealed class HoldStateChangedEventArgs : EventArgs
{
    /// <summary><see langword="true"/> if the call is now on hold; <see langword="false"/> if resumed.</summary>
    public bool  IsOnHold      { get; }

    /// <summary><see langword="true"/> when the change was initiated by the remote party rather than locally.</summary>
    public bool  ByRemoteParty { get; }

    /// <summary>The call whose hold state changed.</summary>
    public ICall Call          { get; }

    internal HoldStateChangedEventArgs(bool isOnHold, bool byRemote, ICall call)
        => (IsOnHold, ByRemoteParty, Call) = (isOnHold, byRemote, call);
}
