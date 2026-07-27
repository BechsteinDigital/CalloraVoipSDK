using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Application.Lines;

/// <summary>
/// A managed phone line paired with the exact forwarding delegates registered on its inbound-notification
/// events, so <see cref="PhoneLineManager"/> can detach them via <c>-=</c> before the line is disposed —
/// otherwise every registered line would leak its two aggregate handlers (#17.9).
/// </summary>
internal sealed record ManagedLine(
    PhoneLine Line,
    EventHandler<IncomingCallEventArgs> OnIncomingCall,
    EventHandler<IncomingMessageEventArgs> OnIncomingMessage);
