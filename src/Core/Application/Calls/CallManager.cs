using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.Application.Calls;

/// <summary>
/// Registry of the SDK's live calls. Exposes the active-call collection, lookup, and
/// add/remove/state-change notifications. Instances are created by the SDK, not by consumers.
/// </summary>
public sealed class CallManager : ICallRegistry, ICallManager
{
    private readonly ConcurrentDictionary<CallId, Call> _calls = new();
    private readonly ILogger _logger;

    // Explicit implementation keeps Register internal on the public CallManager surface
    // while satisfying the Domain-facing ICallRegistry abstraction.
    void ICallRegistry.Register(Call call) => Register(call);

    /// <param name="logger">Logs subscriber faults; defaults to no logging.</param>
    internal CallManager(ILogger<CallManager>? logger = null) => _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>Raised when a new call is registered.</summary>
    public event EventHandler<CallActivityEventArgs>?    CallAdded;

    /// <summary>Raised when a call is removed after reaching <see cref="CallState.Terminated"/>.</summary>
    public event EventHandler<CallActivityEventArgs>?    CallRemoved;

    /// <summary>Raised whenever any registered call changes state; aggregates every call's state changes.</summary>
    public event EventHandler<CallStateChangedEventArgs>? CallStateChanged;

    /// <summary>All calls not yet in <see cref="CallState.Terminated"/>, as a snapshot.</summary>
    public IReadOnlyCollection<ICall> Active =>
        _calls.Values.Where(c => c.State != CallState.Terminated).ToList<ICall>();

    /// <summary>Looks up a registered call by id.</summary>
    /// <param name="id">The call identifier.</param>
    /// <returns>The call, or <see langword="null"/> if no call with that id is registered.</returns>
    public ICall? Find(CallId id) =>
        _calls.TryGetValue(id, out var c) ? c : null;

    internal void Register(Call call)
    {
        _calls[call.CallId] = call;
        call.StateChanged += OnStateChanged;
        Invoke(CallAdded, new CallActivityEventArgs(call), nameof(CallAdded));
    }

    private void OnStateChanged(object? _, CallStateChangedEventArgs e)
    {
        // Keeping a terminated call out of the registry — and unsubscribed from — is this manager's invariant,
        // not a subscriber's business (#165 P2-5). A throwing CallStateChanged handler used to skip both, so
        // the call stayed registered forever: it kept showing up in Find/Active, held its subscription, and
        // counted against nothing that would ever release it. Subscriber throws are isolated and logged rather
        // than propagated, so one bad handler cannot tear down the thread that raised the transition either.
        Invoke(CallStateChanged, e, nameof(CallStateChanged));

        if (e.NewState != CallState.Terminated) return;

        if (!_calls.TryRemove(((Call)e.Call).CallId, out var removed)) return;

        removed.StateChanged -= OnStateChanged;
        Invoke(CallRemoved, new CallActivityEventArgs(removed), nameof(CallRemoved));
    }

    // Raises one aggregated event, isolating a throwing subscriber from the caller and from whatever the
    // caller still has to do. Named so the log says which event the offending handler was on.
    private void Invoke<TArgs>(EventHandler<TArgs>? handler, TArgs args, string eventName)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A {EventName} subscriber threw; the call registry continues regardless.", eventName);
        }
    }
}
