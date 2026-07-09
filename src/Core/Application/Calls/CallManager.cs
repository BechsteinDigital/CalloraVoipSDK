using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk.Core.Application.Calls;

public sealed class CallManager : ICallRegistry
{
    private readonly ConcurrentDictionary<CallId, Call> _calls = new();

    // Explicit implementation keeps Register internal on the public CallManager surface
    // while satisfying the Domain-facing ICallRegistry abstraction.
    void ICallRegistry.Register(Call call) => Register(call);

    internal CallManager()
    {
    }

    public event EventHandler<CallActivityEventArgs>?    CallAdded;
    public event EventHandler<CallActivityEventArgs>?    CallRemoved;
    public event EventHandler<CallStateChangedEventArgs>? CallStateChanged;

    public IReadOnlyCollection<ICall> Active =>
        _calls.Values.Where(c => c.State != CallState.Terminated).ToList<ICall>();

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
