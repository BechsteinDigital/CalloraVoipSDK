namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Point-in-time derived quality of one BUNDLE media stream (CF-004f), attributed to a track by MID (or, when a
/// remote inbound source's payload type was not negotiated, surfaced on its own SSRC). Folds the two directions'
/// metrics for that stream:
/// <list type="bullet">
/// <item><description>
/// <see cref="RoundTripTimeMs"/> and <see cref="PacketLoss"/> are the RTCP outbound metrics (RFC 3550 §6.4.1) —
/// round-trip time and the loss the peer reports on <em>our</em> media — keyed per our sending SSRC.
/// </description></item>
/// <item><description>
/// <see cref="JitterMs"/> is our local receive-side interarrival jitter (RFC 3550 §A.8) for the <em>remote</em>
/// inbound source of this track.
/// </description></item>
/// </list>
/// Every metric is <see langword="null"/> until it is available. <see cref="Ssrc"/> identifies the stream: our
/// sending SSRC when the entry carries outbound metrics, or the remote inbound SSRC for a jitter-only entry.
/// </summary>
/// <param name="Mid">The track's MID, or <see langword="null"/> for an unattributed inbound source.</param>
/// <param name="Ssrc">A representative SSRC for the stream (our sending SSRC, or a remote inbound SSRC).</param>
/// <param name="Kind">The media kind (audio/video/unknown) of the stream.</param>
/// <param name="PacketLoss">The fraction (0..1) of our packets the peer reports lost, or <see langword="null"/>.</param>
/// <param name="JitterMs">Our local receive-side interarrival jitter (ms) for this stream, or <see langword="null"/>.</param>
/// <param name="RoundTripTimeMs">The round-trip time (ms) for our stream, or <see langword="null"/>.</param>
internal readonly record struct BundledStreamQuality(
    string? Mid,
    uint Ssrc,
    BundledStreamKind Kind,
    double? PacketLoss,
    double? JitterMs,
    double? RoundTripTimeMs);

/// <summary>
/// The track identity (MID + kind) a local sending SSRC belongs to, used to attribute a per-SSRC outbound
/// quality metric to a stream.
/// </summary>
/// <param name="Mid">The MID the sending SSRC belongs to.</param>
/// <param name="Kind">The media kind (audio/video) of the track.</param>
internal readonly record struct BundledOutboundStreamIdentity(string Mid, BundledStreamKind Kind);

/// <summary>
/// Folds the outbound (per our sending SSRC) and inbound (per remote SSRC) quality of one MID into a single
/// <see cref="BundledStreamQuality"/>. A simulcast MID merges its encodings' outbound metrics by taking the
/// worst (maximum) RTT and loss; a track's inbound jitter takes the worst across its inbound sources.
/// </summary>
internal sealed class BundledStreamQualityAccumulator
{
    private readonly string _mid;
    private readonly uint _ssrc;
    private readonly BundledStreamKind _kind;
    private double? _packetLoss;
    private double? _jitterMs;
    private double? _roundTripTimeMs;

    /// <summary>Creates the accumulator for one MID, seeded with a representative SSRC and its kind.</summary>
    /// <param name="mid">The track's MID.</param>
    /// <param name="ssrc">A representative SSRC for the stream.</param>
    /// <param name="kind">The media kind (audio/video) of the track.</param>
    public BundledStreamQualityAccumulator(string mid, uint ssrc, BundledStreamKind kind)
    {
        _mid = mid;
        _ssrc = ssrc;
        _kind = kind;
    }

    /// <summary>Merges one sending SSRC's RTT/loss, keeping the worst (maximum) of each across the MID's encodings.</summary>
    public void MergeOutbound(double? roundTripTimeMs, double? packetLoss)
    {
        _roundTripTimeMs = Worst(_roundTripTimeMs, roundTripTimeMs);
        _packetLoss = Worst(_packetLoss, packetLoss);
    }

    /// <summary>Merges one inbound source's jitter, keeping the worst (maximum) across the MID's inbound sources.</summary>
    public void MergeInboundJitter(double jitterMs) => _jitterMs = Worst(_jitterMs, jitterMs);

    /// <summary>Projects the folded per-MID quality.</summary>
    public BundledStreamQuality ToStreamQuality()
        => new(_mid, _ssrc, _kind, _packetLoss, _jitterMs, _roundTripTimeMs);

    // Both loss and RTT are "worse when larger", so the aggregate is the maximum of the available values.
    private static double? Worst(double? current, double? candidate)
    {
        if (candidate is not { } value)
            return current;
        return current is { } c && c >= value ? current : value;
    }
}
