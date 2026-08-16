using System.Collections.Concurrent;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The metric attribution of a bundled session: which stream every SSRC belongs to. It holds the outbound
/// SSRC → (MID, kind) map behind the per-stream quality snapshot, and registers the inbound clock/kind/MID a
/// source resolves from its payload type (RFC 3550 §A.8) with the reception stats.
/// <para>
/// Both used to be construction-time snapshots (#161 P2-11), so a track added mid-call was invisible to the
/// metrics: its inbound sources fell back to an inferred clock with an unknown kind, and its outbound RTT/loss
/// reports could not be attributed to a stream. This collaborator keeps them following the bundle instead.
/// </para>
/// </summary>
/// <remarks>
/// Threading: mutated by the live track-mutation engine under the session's control-plane gate, read
/// concurrently by the metrics snapshot — hence a concurrent map, exposed read-only.
/// </remarks>
internal sealed class BundledStreamAttribution
{
    private readonly ConcurrentDictionary<uint, BundledOutboundStreamIdentity> _outbound;
    private readonly BundledInboundReceptionStats _receptionStats;

    public BundledStreamAttribution(
        ConcurrentDictionary<uint, BundledOutboundStreamIdentity> outboundStreamIdentity,
        BundledInboundReceptionStats receptionStats)
    {
        _outbound = outboundStreamIdentity ?? throw new ArgumentNullException(nameof(outboundStreamIdentity));
        _receptionStats = receptionStats ?? throw new ArgumentNullException(nameof(receptionStats));
    }

    /// <summary>Our sending SSRCs mapped to the track they belong to, for the per-stream quality snapshot.</summary>
    public IReadOnlyDictionary<uint, BundledOutboundStreamIdentity> OutboundIdentity => _outbound;

    /// <summary>
    /// Attributes a track that just went live: its payload type seeds the inbound clock/kind/MID for the
    /// sources that follow (first registration wins — a payload type shared with a live track keeps its
    /// existing attribution), and its sending SSRCs — the single stream, or every simulcast <c>a=rid</c>
    /// encoding — are mapped to its MID.
    /// </summary>
    public void TrackAdded(BundledTrackConfig track, BundledStreamKind kind, uint clockRate)
    {
        ArgumentNullException.ThrowIfNull(track);

        _receptionStats.TryRegisterInboundClock(
            track.PayloadType, new BundledInboundClockDescriptor(clockRate, kind, track.Mid));

        if (track.Encodings.Count > 0)
        {
            foreach (var encoding in track.Encodings)
                _outbound[encoding.Ssrc] = new BundledOutboundStreamIdentity(track.Mid, kind);
        }
        else
        {
            _outbound[track.Ssrc] = new BundledOutboundStreamIdentity(track.Mid, kind);
        }
    }

    /// <summary>
    /// Drops the outbound attribution of a deactivated track: it no longer sends, so a report block still
    /// naming one of its SSRCs belongs to a stream that is gone. The inbound clock entry deliberately stays —
    /// its payload type may be shared with a track that is still live, and a source already admitted under it
    /// keeps its own copy of the clock either way.
    /// </summary>
    public void TrackRemoved(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);

        foreach (var entry in _outbound)
        {
            if (string.Equals(entry.Value.Mid, mid, StringComparison.Ordinal))
                _outbound.TryRemove(entry.Key, out _);
        }
    }
}
