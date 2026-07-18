namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A point-in-time statistics snapshot for a peer connection (the SDK's <c>getStats</c>). Transport
/// counters and derived bitrates are populated today; quality, video and ICE-selection metrics are added in
/// later slices and read <see langword="null"/> until then — a null value means "not yet measured", never a
/// fabricated zero.
/// </summary>
public sealed class WebRtcStats
{
    /// <summary>The peer connection's current lifecycle state.</summary>
    public required PeerConnectionState ConnectionState { get; init; }

    // ── Transport counters (cumulative since the media session was built) ─────────────

    /// <summary>Total RTP packets sent across all tracks.</summary>
    public long PacketsSent { get; init; }

    /// <summary>Total bytes of protected RTP datagrams sent.</summary>
    public long BytesSent { get; init; }

    /// <summary>Total RTP packets received and successfully decrypted.</summary>
    public long PacketsReceived { get; init; }

    /// <summary>Total bytes of decrypted inbound RTP packets.</summary>
    public long BytesReceived { get; init; }

    /// <summary>Outbound sends suppressed before the SRTP keys were installed (fail-closed).</summary>
    public long SuppressedSends { get; init; }

    /// <summary>Inbound datagrams dropped (pre-key, auth failure, replay, or unroutable).</summary>
    public long DroppedDatagrams { get; init; }

    // ── Derived rates ────────────────────────────────────────────────────────────────

    /// <summary>Outgoing bitrate in bits/second since the previous snapshot; <see langword="null"/> on the first snapshot.</summary>
    public double? OutgoingBitrateBps { get; init; }

    /// <summary>Incoming bitrate in bits/second since the previous snapshot; <see langword="null"/> on the first snapshot.</summary>
    public double? IncomingBitrateBps { get; init; }

    // ── Quality (RTCP-derived) — populated by a later slice ──────────────────────────

    /// <summary>Fraction of packets lost (0..1), or <see langword="null"/> until RTCP quality is wired.</summary>
    public double? PacketLoss { get; init; }

    /// <summary>Interarrival jitter in milliseconds, or <see langword="null"/> until RTCP quality is wired.</summary>
    public double? JitterMs { get; init; }

    /// <summary>Round-trip time in milliseconds, or <see langword="null"/> until RTCP quality is wired.</summary>
    public double? RoundTripTimeMs { get; init; }

    // ── Video — populated by a later slice ───────────────────────────────────────────

    /// <summary>Inbound frames per second, or <see langword="null"/> until video metrics are wired.</summary>
    public double? FramesPerSecond { get; init; }

    /// <summary>Frames dropped (e.g. by the reorder buffer), or <see langword="null"/> until video metrics are wired.</summary>
    public long? FramesDropped { get; init; }

    /// <summary>Key frames received, or <see langword="null"/> until video metrics are wired.</summary>
    public long? KeyFrames { get; init; }

    /// <summary>Generic NACK feedback messages, or <see langword="null"/> until video feedback metrics are wired.</summary>
    public long? NackCount { get; init; }

    /// <summary>Picture Loss Indication (PLI) messages, or <see langword="null"/> until video feedback metrics are wired.</summary>
    public long? PliCount { get; init; }

    /// <summary>Full Intra Request (FIR) messages, or <see langword="null"/> until video feedback metrics are wired.</summary>
    public long? FirCount { get; init; }

    // ── ICE — populated by a later slice ─────────────────────────────────────────────

    /// <summary>The ICE agent state, or <see langword="null"/> until ICE stats are wired.</summary>
    public string? IceState { get; init; }

    /// <summary>The selected local ICE candidate, or <see langword="null"/> until ICE stats are wired.</summary>
    public string? SelectedLocalCandidate { get; init; }

    /// <summary>The selected remote ICE candidate, or <see langword="null"/> until ICE stats are wired.</summary>
    public string? SelectedRemoteCandidate { get; init; }

    // ── Congestion control — populated by a later slice ──────────────────────────────

    /// <summary>
    /// Estimated available outgoing bitrate in bits/second from congestion control, or
    /// <see langword="null"/> until transport-cc is wired through the bundle.
    /// </summary>
    public long? AvailableOutgoingBitrateBps { get; init; }
}
