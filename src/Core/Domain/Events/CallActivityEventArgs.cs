using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for call-registry activity events (a call being added or removed).</summary>
public sealed class CallActivityEventArgs : EventArgs
{
    /// <summary>The call that was added or removed.</summary>
    public ICall Call { get; }
    internal CallActivityEventArgs(ICall call) => Call = call;
}
