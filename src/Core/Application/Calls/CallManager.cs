using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk.Core.Application.Calls;

/// <summary>
/// Registry of the SDK's live calls. Exposes the active-call collection, lookup, and
/// add/remove/state-change notifications. Instances are created by the SDK, not by consumers.
/// </summary>
public sealed class CallManager : ICallRegistry, ICallManager
{
    private readonly ConcurrentDictionary<CallId, Call> _calls = new();

    // Explicit implementation keeps Register internal on the public CallManager surface
    // while satisfying the Domain-facing ICallRegistry abstraction.
    void ICallRegistry.Register(Call call) => Register(call);

    internal CallManager()
    {
    }

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
        CallAdded?.Invoke(this, new CallActivityEventArgs(call));
    }

    private void OnStateChanged(object? _, CallStateChangedEventArgs e)
    {
        CallStateChanged?.Invoke(this, e);

        if (e.NewState != CallState.Terminated) return;

        if (_calls.TryRemove(((Call)e.Call).CallId, out var removed))
        {
            removed.StateChanged -= OnStateChanged;
            CallRemoved?.Invoke(this, new CallActivityEventArgs(removed));
        }
    }
}
