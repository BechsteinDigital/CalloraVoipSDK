namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// One observed packet arrival for transport-wide congestion control: the transport-wide sequence
/// number stamped in the RTP header extension (RFC 8285) paired with a monotonic arrival timestamp.
/// The feedback builder turns a batch of these into a transport-cc RTCP report
/// (draft-holmer-rmcat-transport-wide-cc-extensions-01).
/// </summary>
internal readonly record struct TransportCcArrival
{
    /// <summary>The transport-wide sequence number the sender stamped on the packet.</summary>
    public required ushort SequenceNumber { get; init; }

    /// <summary>
    /// Monotonic arrival timestamp in <see cref="System.Diagnostics.Stopwatch"/> ticks. Only
    /// differences between arrivals are meaningful; the absolute origin is arbitrary.
    /// </summary>
    public required long ArrivalTimestamp { get; init; }
}
