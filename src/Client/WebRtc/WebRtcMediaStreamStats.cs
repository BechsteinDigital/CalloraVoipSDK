namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A per-media-stream quality snapshot within a peer connection's <see cref="WebRtcStats"/> (the SDK's
/// <c>getStats</c> per-stream breakdown). One entry per audio/video track (folded by MID), plus one per remote
/// inbound source whose payload type could not be attributed to a track. The two directions carry different
/// metrics — <see cref="RoundTripTimeMs"/> and <see cref="PacketLoss"/> describe <em>our outbound</em> stream
/// (keyed per our sending SSRC, RFC 3550 §6.4.1), while <see cref="JitterMs"/> is our <em>local receive-side</em>
/// jitter for the <em>remote inbound</em> source (RFC 3550 §A.8). A metric reads <see langword="null"/> until it
/// is available for that stream — never a fabricated zero.
/// </summary>
public sealed class WebRtcMediaStreamStats
{
    /// <summary>
    /// The track's MID (<c>a=mid</c>), or <see langword="null"/> for a remote inbound source whose payload type
    /// was not negotiated (so it could not be attributed to a track).
    /// </summary>
    public string? Mid { get; init; }

    /// <summary>
    /// A representative synchronisation source for the stream: our sending SSRC when the entry carries outbound
    /// metrics, or the remote inbound SSRC for a jitter-only entry.
    /// </summary>
    public uint Ssrc { get; init; }

    /// <summary>The media kind: <c>"audio"</c>, <c>"video"</c>, or <c>"unknown"</c> (unattributed inbound source).</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Fraction of our outbound packets the peer reports lost on this stream (0..1) via its RTCP reception report
    /// (RFC 3550 §6.4.1), or <see langword="null"/> until the peer has reported on this stream.
    /// </summary>
    public double? PacketLoss { get; init; }

    /// <summary>
    /// Our local receive-side interarrival jitter (ms) for this stream's remote inbound source (RFC 3550 §A.8),
    /// or <see langword="null"/> until an inbound clock is established for it.
    /// </summary>
    public double? JitterMs { get; init; }

    /// <summary>
    /// Round-trip time (ms) for our outbound stream, derived from the peer's echoed LSR/DLSR (RFC 3550 §6.4.1),
    /// or <see langword="null"/> until a report block echoing one of our Sender Reports has arrived.
    /// </summary>
    public double? RoundTripTimeMs { get; init; }
}
