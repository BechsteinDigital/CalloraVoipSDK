namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;

/// <summary>
/// Result of registering one inbound request in the server transaction engine.
/// </summary>
internal readonly record struct SipServerTransactionRegistration
{
    /// <summary>
    /// True when inbound request matched an existing transaction.
    /// </summary>
    public bool IsRetransmission { get; init; }

    /// <summary>
    /// True when request was ACK and should only stop retransmit timers.
    /// </summary>
    public bool IsAck { get; init; }

    /// <summary>
    /// True when the request was dropped because the server-transaction table is at capacity (#158 P1-7).
    /// The upper layer must not process it — a new transaction was deliberately not created under overload.
    /// </summary>
    public bool IsOverCapacity { get; init; }

    /// <summary>
    /// True when upper signaling layer should continue processing request.
    /// </summary>
    public bool ShouldProcess => !IsRetransmission && !IsAck && !IsOverCapacity;
}

