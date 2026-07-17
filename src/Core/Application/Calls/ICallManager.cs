using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk.Core.Application.Calls;

/// <summary>
/// Registry of the SDK's live calls. Exposes the active-call collection, lookup, and
/// add/remove/state-change notifications.
/// </summary>
public interface ICallManager
{
    /// <summary>Raised when a new call is registered.</summary>
    event EventHandler<CallActivityEventArgs>? CallAdded;

    /// <summary>Raised when a call is removed after reaching <see cref="CallState.Terminated"/>.</summary>
    event EventHandler<CallActivityEventArgs>? CallRemoved;

    /// <summary>Raised whenever any registered call changes state; aggregates every call's state changes.</summary>
    event EventHandler<CallStateChangedEventArgs>? CallStateChanged;

    /// <summary>All calls not yet in <see cref="CallState.Terminated"/>, as a snapshot.</summary>
    IReadOnlyCollection<ICall> Active { get; }

    /// <summary>Looks up a registered call by id.</summary>
    /// <param name="id">The call identifier.</param>
    /// <returns>The call, or <see langword="null"/> if no call with that id is registered.</returns>
    ICall? Find(CallId id);
}
