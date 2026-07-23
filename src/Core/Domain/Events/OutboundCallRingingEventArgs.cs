using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>
/// Payload for the line <c>OutboundCallRinging</c> event: the outbound call reached Ringing (a 180/183
/// early dialog) before the 200 OK. Observe early media / call state via <see cref="Call"/> — analogous
/// to SIPSorcery's ClientCallRinging. Fires while <c>DialAsync</c> is still awaiting the answer.
/// </summary>
public sealed class OutboundCallRingingEventArgs : EventArgs
{
    /// <summary>The outbound call, now ringing (not yet Connected).</summary>
    public ICall Call { get; }
    internal OutboundCallRingingEventArgs(ICall call) => Call = call;
}
