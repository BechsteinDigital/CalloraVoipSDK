namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// The active-call registry as the Domain needs it: a phone line registers each call it
/// creates and enumerates the calls that belong to it. Implemented by the Application's call
/// manager, so the Domain depends only on this abstraction rather than on the service itself.
/// </summary>
internal interface ICallRegistry
{
    /// <summary>Registers a newly created call so it becomes globally tracked and active.</summary>
    void Register(Call call);

    /// <summary>All currently active calls across every line.</summary>
    IReadOnlyCollection<ICall> Active { get; }
}
