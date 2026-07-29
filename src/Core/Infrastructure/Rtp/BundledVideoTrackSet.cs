using System.Collections.Concurrent;
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
/// The tracks share the one receive loop: their <see cref="BundledVideoTrack.OnRtpPacket"/> sinks are
/// registered per MID on the router, and <see cref="OnRtcpPackets"/> fans a single already-decoded compound
/// to every track on that same receive-loop thread, preserving each track's single-consumer confinement.
/// Thread-safe and live-extensible (P3b): the MID→track map is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>, so <see cref="TryAdd"/>/<see cref="Remove"/> can add or
/// deactivate a track mid-call while the receive loop reads the map lock-free (<see cref="Find"/>,
/// <see cref="OnRtcpPackets"/>, <see cref="SnapshotStats"/>) — its enumeration is a snapshot, so a concurrent
/// add/remove never tears a fan-out. A small side list keeps the MIDs in insertion order (primary first) so
/// <see cref="Tracks"/>/<see cref="Mids"/> stay stably ordered across live add/remove; it is guarded by its
/// own lock and touched only on structural change and diagnostics reads, never on the media hot path. The
/// <see cref="Primary"/> is fixed at construction (the first track built) and a live add never changes it, so
/// the mid-less facade stays pinned to the original track.
/// </remarks>
internal sealed class BundledVideoTrackSet
{
    private readonly ConcurrentDictionary<string, BundledVideoTrack> _byMid;
    private readonly BundledVideoTrack? _primary;

    // The MIDs in insertion order (primary first), so Tracks/Mids stay stably ordered across live add/remove
    // (a ConcurrentDictionary does not guarantee insertion-order enumeration). Structural mutations (ctor,
    // TryAdd, Remove) take this lock; the receive-loop reads — Find, OnRtcpPackets, SnapshotStats — stay
    // lock-free on _byMid. Mids/Tracks snapshot this list under the lock (diagnostics, not the media hot path).
    private readonly object _orderGate = new();
    private readonly List<string> _midOrder = [];

    /// <summary>Creates an empty set (an audio-only bundle).</summary>
    public BundledVideoTrackSet()
    {
        _byMid = new ConcurrentDictionary<string, BundledVideoTrack>(StringComparer.Ordinal);
        _primary = null;
    }

    /// <summary>Creates the set from the video tracks, in the order they were built (first = primary).</summary>
    /// <param name="tracks">The video tracks keyed by their MID; must have distinct MIDs.</param>
    /// <exception cref="ArgumentException">Two tracks share a MID.</exception>
    public BundledVideoTrackSet(IReadOnlyList<(string Mid, BundledVideoTrack Track)> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        _byMid = new ConcurrentDictionary<string, BundledVideoTrack>(StringComparer.Ordinal);
        foreach (var (mid, track) in tracks)
        {
            if (!_byMid.TryAdd(mid, track))
                throw new ArgumentException($"Duplicate video MID '{mid}' in the bundle.", nameof(tracks));
            _midOrder.Add(mid);
            _primary ??= track;
        }
    }

    /// <summary>
    /// Registers a video track added mid-call (P3b) under its MID. Live and lock-free against the receive
    /// loop; the <see cref="Primary"/> is unchanged (a live add never becomes the primary). Returns
    /// <see langword="false"/> when a track is already registered for <paramref name="mid"/>.
    /// </summary>
    /// <param name="mid">The MID of the newly added video track.</param>
    /// <param name="track">The built track to register as the router sink / feedback target for that MID.</param>
    public bool TryAdd(string mid, BundledVideoTrack track)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(track);
        // Publish to the lock-free receive-path map first; only then record the order (so a reader that sees
        // the MID in the order list can always resolve it). A losing TryAdd leaves the order list untouched.
        if (!_byMid.TryAdd(mid, track))
            return false;
        lock (_orderGate)
            _midOrder.Add(mid);
        return true;
    }

    /// <summary>
    /// Deactivates the video track for <paramref name="mid"/> (P3b), removing it from the set so it no longer
    /// receives inbound frames or RTCP feedback. Live and lock-free against the receive loop; the removed
    /// track is returned so the caller can dispose it (releasing its send lock and in-flight feedback), or
    /// <see langword="null"/> when no track was registered for that MID. Removing the primary is possible but
    /// leaves <see cref="Primary"/> pointing at the (now-removed) original track — the caller controls that.
    /// </summary>
    public BundledVideoTrack? Remove(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        if (!_byMid.TryRemove(mid, out var track))
            return null;
        lock (_orderGate)
            _midOrder.Remove(mid);
        return track;
    }

    /// <summary>Whether the bundle carries at least one video track.</summary>
    public bool Any => !_byMid.IsEmpty;

    /// <summary>The number of video tracks on the bundle.</summary>
    public int Count => _byMid.Count;

    /// <summary>
    /// The primary video track — the first one built — or <see langword="null"/> for an audio-only bundle.
    /// The mid-less <c>SendVideoFrameAsync</c>/<c>RequestVideoKeyFrameAsync</c> overloads and the legacy
    /// mid-less <c>VideoFrameReceived</c> event address this one (backward compatibility with the 1+1 path).
    /// </summary>
    public BundledVideoTrack? Primary => _primary;

    /// <summary>
    /// The video tracks as a point-in-time snapshot in insertion order (primary first), skipping any MID
    /// removed by <see cref="Remove"/> concurrently. Diagnostics/enumeration, not the media hot path.
    /// </summary>
    public IEnumerable<BundledVideoTrack> Tracks
    {
        get
        {
            foreach (var mid in Mids)
                if (_byMid.TryGetValue(mid, out var track))
                    yield return track;
        }
    }

    /// <summary>The video track MIDs as a point-in-time snapshot in insertion order (primary first).</summary>
    public IReadOnlyList<string> Mids
    {
        get { lock (_orderGate) return _midOrder.ToArray(); }
    }

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
        if (_byMid.IsEmpty)
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
