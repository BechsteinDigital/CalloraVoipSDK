using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Application.Lines;

/// <summary>
/// Registry of the SDK's phone lines. Registers new SIP accounts, unregisters lines, exposes the
/// current line collection, and aggregates each line's inbound-call notifications.
/// </summary>
public interface IPhoneLineManager : IDisposable
{
    /// <summary>Raised when any managed line receives an inbound call; aggregates every line's incoming calls.</summary>
    event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <summary>Raised when any managed line receives an inbound SIP MESSAGE (RFC 3428); aggregates every line's messages.</summary>
    event EventHandler<IncomingMessageEventArgs>? IncomingMessage;

    /// <summary>
    /// Registers <paramref name="account"/> as a new phone line and starts its SIP registration.
    /// </summary>
    /// <param name="account">The SIP account to register.</param>
    /// <returns>The newly created line; watch <see cref="IPhoneLine.StateChanged"/> for registration progress.</returns>
    IPhoneLine Register(SipAccount account);

    /// <summary>
    /// Unregisters and disposes the line with the given id. No-op if the id is unknown.
    /// </summary>
    /// <param name="id">The line to unregister.</param>
    /// <param name="ct">Cancels the unregister request.</param>
    Task UnregisterAsync(LineId id, CancellationToken ct = default);

    /// <summary>All currently registered lines, as a snapshot.</summary>
    IReadOnlyCollection<IPhoneLine> All { get; }
}
