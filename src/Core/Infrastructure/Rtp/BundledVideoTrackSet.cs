using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The set of video tracks on one BUNDLE media session (P2b: N video m-lines, RFC 8843 §9). Each
/// <see cref="BundledVideoTrack"/> is keyed by its own MID and carried on its own bundle-wide-distinct
/// SSRC(s); the set owns them, dispatches inbound RTCP to all of them, and aggregates their point-in-time
/// counters. The first track built is the <see cref="Primary"/> — the track the single-track, mid-less
/// send/receive facade addresses for backward compatibility with the pre-P2b 1-audio-1-video path.
/// </summary>
/// <remarks>
/// Insertion order is preserved (the underlying dictionary keeps it) so the primary is stable across
/// snapshots. The tracks share the one receive loop: their <see cref="BundledVideoTrack.OnRtpPacket"/>
/// sinks are registered per MID on the router, and <see cref="OnRtcpPackets"/> fans a single already-decoded
/// compound to every track on that same receive-loop thread, preserving each track's single-consumer
/// confinement. This type performs no locking — it is written once at construction and only read afterwards.
/// </remarks>
internal sealed class BundledVideoTrackSet
{
    private readonly Dictionary<string, BundledVideoTrack> _byMid;
    private readonly BundledVideoTrack? _primary;

    /// <summary>Creates an empty set (an audio-only bundle).</summary>
    public BundledVideoTrackSet()
    {
        _byMid = new Dictionary<string, BundledVideoTrack>(StringComparer.Ordinal);
        _primary = null;
    }

    /// <summary>Creates the set from the video tracks, in the order they were built (first = primary).</summary>
    /// <param name="tracks">The video tracks keyed by their MID; must have distinct MIDs.</param>
    /// <exception cref="ArgumentException">Two tracks share a MID.</exception>
    public BundledVideoTrackSet(IReadOnlyList<(string Mid, BundledVideoTrack Track)> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        _byMid = new Dictionary<string, BundledVideoTrack>(tracks.Count, StringComparer.Ordinal);
        foreach (var (mid, track) in tracks)
        {
            if (!_byMid.TryAdd(mid, track))
                throw new ArgumentException($"Duplicate video MID '{mid}' in the bundle.", nameof(tracks));
            _primary ??= track;
        }
    }

    /// <summary>Whether the bundle carries at least one video track.</summary>
    public bool Any => _byMid.Count > 0;

    /// <summary>The number of video tracks on the bundle.</summary>
    public int Count => _byMid.Count;

    /// <summary>
    /// The primary video track — the first one built — or <see langword="null"/> for an audio-only bundle.
    /// The mid-less <c>SendVideoFrameAsync</c>/<c>RequestVideoKeyFrameAsync</c> overloads and the legacy
    /// mid-less <c>VideoFrameReceived</c> event address this one (backward compatibility with the 1+1 path).
    /// </summary>
    public BundledVideoTrack? Primary => _primary;

    /// <summary>The video tracks in build order (primary first).</summary>
    public IEnumerable<BundledVideoTrack> Tracks => _byMid.Values;

    /// <summary>The video track MIDs in build order (primary first).</summary>
    public IReadOnlyList<string> Mids => _byMid.Keys.ToArray();

    /// <summary>Resolves the track for a MID, or <see langword="null"/> when the bundle has no such video track.</summary>
    public BundledVideoTrack? Find(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        return _byMid.TryGetValue(mid, out var track) ? track : null;
    }

    /// <summary>
    /// Fans one already-decoded inbound RTCP compound to every video track for feedback (PLI/FIR → key-frame
    /// request; Generic NACK → RTX retransmit, RFC 4585/4588). Each track filters the compound to its own SSRC
    /// internally, so a NACK for one track's SSRC never resends another's. Runs on the shared receive loop.
    /// </summary>
    public void OnRtcpPackets(IReadOnlyList<RtcpPacket> packets)
    {
        foreach (var track in _byMid.Values)
            track.OnRtcpPackets(packets);
    }

    /// <summary>
    /// Aggregate video receive/feedback counters across all tracks (S4), or <see langword="null"/> for each
    /// when the bundle has no video track. Sums are cumulative since session creation.
    /// </summary>
    public BundledVideoAggregateStats SnapshotStats()
    {
        if (_byMid.Count == 0)
            return default;

        long framesReceived = 0, keyFrames = 0, framesDropped = 0, nacksSent = 0, plisSent = 0;
        foreach (var track in _byMid.Values)
        {
            framesReceived += track.FramesReceived;
            keyFrames += track.KeyFrames;
            framesDropped += track.FramesDropped;
            nacksSent += track.NacksSent;
            plisSent += track.PlisSent;
        }

        return new BundledVideoAggregateStats(framesReceived, keyFrames, framesDropped, nacksSent, plisSent);
    }

    /// <summary>Disposes every track (releasing send locks and cancelling in-flight feedback).</summary>
    public void Dispose()
    {
        foreach (var track in _byMid.Values)
            track.Dispose();
    }
}

/// <summary>
/// Aggregated video counters across all of a bundle's video tracks (P2b). A <see langword="default"/>
/// value (all zero) is produced for an audio-only bundle; the session surfaces the counters as
/// <see langword="null"/> in that case.
/// </summary>
/// <param name="FramesReceived">Total reassembled inbound frames across all tracks.</param>
/// <param name="KeyFrames">Total inbound key frames across all tracks.</param>
/// <param name="FramesDropped">Total frames dropped on a reorder discontinuity across all tracks.</param>
/// <param name="NacksSent">Total Generic NACK feedback messages sent across all tracks.</param>
/// <param name="PlisSent">Total PLI key-frame requests sent across all tracks.</param>
internal readonly record struct BundledVideoAggregateStats(
    long FramesReceived,
    long KeyFrames,
    long FramesDropped,
    long NacksSent,
    long PlisSent);
