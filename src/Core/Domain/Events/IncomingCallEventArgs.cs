using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for the line <c>IncomingCall</c> event.</summary>
public sealed class IncomingCallEventArgs : EventArgs
{
    /// <summary>The inbound call, already ringing; accept or reject it via this instance.</summary>
    public ICall Call { get; }
    internal IncomingCallEventArgs(ICall call) => Call = call;
}
