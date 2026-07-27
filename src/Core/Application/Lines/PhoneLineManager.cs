using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Application.Lines;

/// <summary>
/// Registry of the SDK's phone lines. Registers new SIP accounts, unregisters lines, exposes the
/// current line collection, and aggregates each line's inbound-call notifications. Instances are
/// created by the SDK, not by consumers.
/// </summary>
public sealed class PhoneLineManager : IPhoneLineManager
{
    private readonly Func<SipAccount, PhoneLine> _factory;
    private readonly ConcurrentDictionary<LineId, ManagedLine> _lines = new();

    /// <summary>Raised when any managed line receives an inbound call; aggregates every line's incoming calls.</summary>
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <summary>Raised when any managed line receives an inbound SIP MESSAGE; aggregates every line's messages.</summary>
    public event EventHandler<IncomingMessageEventArgs>? IncomingMessage;

    internal PhoneLineManager(Func<SipAccount, PhoneLine> factory)
        => _factory = factory;

    /// <summary>
    /// Registers <paramref name="account"/> as a new phone line and starts its SIP registration.
    /// </summary>
    /// <param name="account">The SIP account to register.</param>
    /// <returns>The newly created line; watch <see cref="IPhoneLine.StateChanged"/> for registration progress.</returns>
    public IPhoneLine Register(SipAccount account)
    {
        var line = _factory(account);
        // Named delegates (not throwaway lambdas) so the exact same instances can be detached on
        // Unregister/Dispose; a fresh lambda per subscribe could never be removed (#17.9).
        EventHandler<IncomingCallEventArgs> onIncomingCall = (s, e) => IncomingCall?.Invoke(s, e);
        EventHandler<IncomingMessageEventArgs> onIncomingMessage = (s, e) => IncomingMessage?.Invoke(s, e);
        line.IncomingCall += onIncomingCall;
        line.IncomingMessage += onIncomingMessage;
        _lines[line.LineId] = new ManagedLine(line, onIncomingCall, onIncomingMessage);
        line.StartRegistration();
        return line;
    }

    /// <summary>
    /// Unregisters and disposes the line with the given id. No-op if the id is unknown.
    /// </summary>
    /// <param name="id">The line to unregister.</param>
    /// <param name="ct">Cancels the unregister request.</param>
    public async Task UnregisterAsync(LineId id, CancellationToken ct = default)
    {
        if (_lines.TryRemove(id, out var managed))
        {
            DetachAggregateHandlers(managed);
            await managed.Line.UnregisterAsync(ct);
            managed.Line.Dispose();
        }
    }

    /// <summary>All currently registered lines, as a snapshot.</summary>
    public IReadOnlyCollection<IPhoneLine> All => _lines.Values.Select(m => (IPhoneLine)m.Line).ToList();

    /// <summary>Unregisters and disposes every managed line.</summary>
    public void Dispose()
    {
        foreach (var managed in _lines.Values)
        {
            DetachAggregateHandlers(managed);
            managed.Line.Dispose();
        }

        _lines.Clear();
    }

    // Detaches the aggregate forwarding handlers before the line is disposed, so no per-line handler leaks
    // onto the manager's IncomingCall/IncomingMessage events (#17.9).
    private void DetachAggregateHandlers(ManagedLine managed)
    {
        managed.Line.IncomingCall -= managed.OnIncomingCall;
        managed.Line.IncomingMessage -= managed.OnIncomingMessage;
    }
}
