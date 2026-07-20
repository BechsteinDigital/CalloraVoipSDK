namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A point-in-time statistics snapshot for a peer connection (the SDK's <c>getStats</c>). Transport
/// counters, derived bitrates and the ICE state/selected pair are populated today; quality, video and
/// congestion-control metrics are added in later slices and read <see langword="null"/> until then — a null
/// value means "not yet measured", never a fabricated zero.
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

    // ── Quality (RTCP-derived) ───────────────────────────────────────────────────────

    /// <summary>
    /// Fraction of our outbound packets the peer reports lost (0..1) via its RTCP reception report
    /// (RFC 3550 §6.4.1), or <see langword="null"/> until the peer has reported on our media. This is the
    /// session aggregate — the worst (maximum) loss across our sending streams; the per-stream breakdown is in
    /// <see cref="MediaStreams"/>.
    /// </summary>
    public double? PacketLoss { get; init; }

    /// <summary>
    /// Our local receive-side interarrival jitter in milliseconds (RFC 3550 §A.8) — the WebRTC
    /// <c>getStats</c> inbound-rtp jitter — or <see langword="null"/> until an inbound clock rate is established
    /// (no media received yet). Converted from RTP timestamp units with the negotiated clock rate. This is the
    /// session aggregate — the worst (maximum) across inbound sources; the per-stream breakdown is in
    /// <see cref="MediaStreams"/>.
    /// </summary>
    public double? JitterMs { get; init; }

    /// <summary>
    /// Round-trip time in milliseconds derived from the peer's echoed LSR/DLSR (RFC 3550 §6.4.1), or
    /// <see langword="null"/> until a report block echoing one of our Sender Reports has arrived. This is the
    /// session aggregate — the worst (maximum) RTT across our sending streams; the per-stream breakdown is in
    /// <see cref="MediaStreams"/>.
    /// </summary>
    public double? RoundTripTimeMs { get; init; }

    /// <summary>
    /// Per-media-stream quality (CF-004f): one entry per audio/video track (folded by MID) carrying the RTT and
    /// loss the peer reports on our outbound stream plus our local receive-side jitter for that track's remote
    /// inbound source, and one entry per remote inbound source whose payload type could not be attributed to a
    /// track. Empty until a session is built or a per-stream metric is available. The scalar
    /// <see cref="RoundTripTimeMs"/>/<see cref="PacketLoss"/>/<see cref="JitterMs"/> are the worst-of aggregate
    /// across these streams.
    /// </summary>
    public IReadOnlyList<WebRtcMediaStreamStats> MediaStreams { get; init; } = [];

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

    // ── ICE ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A W3C <c>RTCIceConnectionState</c>-style label (new/checking/connected/disconnected/failed/closed)
    /// derived from the peer's connectivity — the bundle uses single-candidate selection, not a separate
    /// ICE-checklist FSM.
    /// </summary>
    public string? IceState { get; init; }

    /// <summary>The selected local candidate (the bound local media endpoint), or <see langword="null"/> before the transport binds.</summary>
    public string? SelectedLocalCandidate { get; init; }

    /// <summary>The selected remote candidate (the resolved remote media endpoint), or <see langword="null"/> before one is set.</summary>
    public string? SelectedRemoteCandidate { get; init; }

    // ── Congestion control — populated by a later slice ──────────────────────────────

    /// <summary>
    /// Estimated available outgoing bitrate in bits/second from congestion control, or
    /// <see langword="null"/> until transport-cc is wired through the bundle.
    /// </summary>
    public long? AvailableOutgoingBitrateBps { get; init; }
}
