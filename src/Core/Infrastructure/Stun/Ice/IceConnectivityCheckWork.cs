namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>One bounded work item owned by <see cref="IceConnectivityCheckPacer"/>.</summary>
internal sealed class IceConnectivityCheckWork
{
    /// <summary>Scheduling class; lower enum values have precedence.</summary>
    public required IceConnectivityCheckKind Kind { get; init; }

    /// <summary>ICE pair priority used inside nomination and ordinary queues.</summary>
    public required long Priority { get; init; }

    /// <summary>Sends the check and awaits its transaction outcome.</summary>
    public required Func<CancellationToken, Task<bool>> Execute { get; init; }

    /// <summary>Consumes the outcome without blocking the pacer.</summary>
    public required Action<bool> Complete { get; init; }
}
