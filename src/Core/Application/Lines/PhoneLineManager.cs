using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;

    /// <summary>Raised when any managed line receives an inbound call; aggregates every line's incoming calls.</summary>
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <summary>Raised when any managed line receives an inbound SIP MESSAGE; aggregates every line's messages.</summary>
    public event EventHandler<IncomingMessageEventArgs>? IncomingMessage;

    /// <param name="factory">Builds the line for an account.</param>
    /// <param name="logger">Logs teardown faults; defaults to no logging.</param>
    internal PhoneLineManager(Func<SipAccount, PhoneLine> factory, ILogger<PhoneLineManager>? logger = null)
    {
        _factory = factory;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

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
        var managed = new ManagedLine(line, onIncomingCall, onIncomingMessage);
        _lines[line.LineId] = managed;

        // Publish first, then start: an inbound INVITE may arrive the moment registration succeeds, and it
        // has to find the line already in the registry. But a failing start must not leave it there (#165
        // P2-6) — the caller has no handle to clean up with, since Register throws instead of returning one,
        // so the line would sit in All() forever, subscribed and never registered.
        try
        {
            line.StartRegistration();
        }
        catch
        {
            _lines.TryRemove(line.LineId, out _);
            DetachAggregateHandlers(managed);
            line.Dispose();
            throw;
        }

        return line;
    }

    /// <summary>
    /// Unregisters and disposes the line with the given id. No-op if the id is unknown.
    /// </summary>
    /// <param name="id">The line to unregister.</param>
    /// <param name="ct">Cancels the unregister request.</param>
    public async Task UnregisterAsync(LineId id, CancellationToken ct = default)
    {
        if (!_lines.TryRemove(id, out var managed))
            return;

        DetachAggregateHandlers(managed);
        try
        {
            // Best-effort on the wire: the REGISTER Expires:0 may fail or be cancelled, and the caller
            // should hear about it.
            await managed.Line.UnregisterAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Local teardown is not best-effort (#165 P2-6). The line is already out of the registry, so a
            // throwing or cancelled unregister used to leave it invisible but fully alive — its transport,
            // its timers and its subscriptions all still running, with nothing left holding a reference.
            managed.Line.Dispose();
        }
    }

    /// <summary>All currently registered lines, as a snapshot.</summary>
    public IReadOnlyCollection<IPhoneLine> All => _lines.Values.Select(m => (IPhoneLine)m.Line).ToList();

    /// <summary>Unregisters and disposes every managed line.</summary>
    public void Dispose()
    {
        // One line that throws on Dispose must not strand every line behind it (#165 P2-6): each is torn
        // down on its own, and the fault is logged rather than propagated out of Dispose.
        foreach (var managed in _lines.Values)
        {
            DetachAggregateHandlers(managed);
            try
            {
                managed.Line.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Disposing phone line {LineId} failed; continuing with the remaining lines.",
                    managed.Line.LineId);
            }
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
