namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// Per-packet arrival status inside a transport-wide congestion-control feedback message
/// (RTPFB PT=205, FMT=15 — draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1). One
/// entry per transport-wide sequence number the feedback reports on, in ascending order
/// starting at the message's base sequence number.
/// </summary>
internal readonly record struct RtcpTransportFeedbackStatus
{
    /// <summary>The transport-wide sequence number (from the RTP header extension) this status refers to.</summary>
    public required ushort SequenceNumber { get; init; }

    /// <summary>
    /// True when the packet was received. A not-received packet carries no arrival delta and
    /// <see cref="DeltaTicks"/> is ignored.
    /// </summary>
    public required bool Received { get; init; }

    /// <summary>
    /// Arrival time relative to the previous received packet (or, for the first received packet,
    /// to the message reference time), in 250 µs ticks (draft §3.1.5). Non-negative and 0..255 for a
    /// small delta encoded in one byte; a value outside that range is encoded as a signed two-byte
    /// large/negative delta (−32768..32767). Ignored when <see cref="Received"/> is false.
    /// </summary>
    public int DeltaTicks { get; init; }
}
