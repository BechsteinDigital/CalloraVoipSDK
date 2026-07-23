using CalloraVoipSdk.Core.Domain.Messages;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for the line <c>IncomingMessage</c> event (RFC 3428 SIP MESSAGE).</summary>
public sealed class IncomingMessageEventArgs : EventArgs
{
    /// <summary>The received pager-mode instant message. The SDK has already answered it 200 OK.</summary>
    public SipInstantMessage Message { get; }

    internal IncomingMessageEventArgs(SipInstantMessage message) => Message = message;
}
