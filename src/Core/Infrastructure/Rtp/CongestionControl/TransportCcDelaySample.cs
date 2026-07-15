namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// One inter-packet one-way delay gradient derived from a transport-cc report: how much more (or
/// less) time elapsed between this packet's arrival and the previous correlated packet's arrival
/// than between their send times. A positive gradient means packets are arriving slower than they
/// were sent — a growing queue and the early congestion signal that delay-based control reacts to;
/// a negative gradient means the queue is draining. Clock offset between the two endpoints cancels
/// because only differences are used.
/// </summary>
internal readonly record struct TransportCcDelaySample
{
    /// <summary>The transport-wide sequence number this sample was computed at.</summary>
    public required ushort SequenceNumber { get; init; }

    /// <summary>
    /// The delay gradient in microseconds: <c>(arrival − prevArrival) − (send − prevSend)</c>.
    /// Positive = increasing one-way delay (congestion building), negative = delay shrinking.
    /// </summary>
    public required long DelayGradientMicros { get; init; }
}
