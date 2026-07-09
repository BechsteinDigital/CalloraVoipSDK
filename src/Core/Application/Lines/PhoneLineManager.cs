using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Application.Lines;

/// <summary>
/// Registry of the SDK's phone lines. Registers new SIP accounts, unregisters lines, exposes the
/// current line collection, and aggregates each line's inbound-call notifications. Instances are
/// created by the SDK, not by consumers.
/// </summary>
public sealed class PhoneLineManager : IDisposable
{
    private readonly Func<SipAccount, PhoneLine> _factory;
    private readonly ConcurrentDictionary<LineId, PhoneLine> _lines = new();

    /// <summary>Raised when any managed line receives an inbound call; aggregates every line's incoming calls.</summary>
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

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
        line.IncomingCall += (s, e) => IncomingCall?.Invoke(s, e);
        _lines[line.LineId] = line;
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
        if (_lines.TryRemove(id, out var line))
        {
            await line.UnregisterAsync(ct);
            line.Dispose();
        }
    }

    /// <summary>All currently registered lines, as a snapshot.</summary>
    public IReadOnlyCollection<IPhoneLine> All => _lines.Values.ToList<IPhoneLine>();

    /// <summary>Unregisters and disposes every managed line.</summary>
    public void Dispose()
    {
        foreach (var line in _lines.Values) line.Dispose();
        _lines.Clear();
    }
}
