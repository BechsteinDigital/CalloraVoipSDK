namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// One packet's reconstructed outcome from a transport-cc feedback report
/// (draft-holmer-rmcat-transport-wide-cc-extensions-01): whether the remote received it and, if so,
/// the arrival time the report encodes. Arrival times are in microseconds on the reporting peer's
/// clock (relative to the report's reference time) — only their differences are meaningful; a
/// congestion-control estimator correlates them with local send times.
/// </summary>
internal readonly record struct TransportCcFeedbackResult
{
    /// <summary>The transport-wide sequence number this outcome refers to.</summary>
    public required ushort SequenceNumber { get; init; }

    /// <summary>True when the remote reported the packet as received.</summary>
    public required bool Received { get; init; }

    /// <summary>
    /// Reconstructed arrival time in microseconds on the reporting peer's clock; meaningful only when
    /// <see cref="Received"/> is true (zero otherwise).
    /// </summary>
    public long ArrivalMicros { get; init; }
}
