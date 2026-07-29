namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Tracks the outbound synchronisation sources live on a bundled media session (RFC 3550 §8.1), keyed by the
/// MID that owns them, so a mid-call renegotiation can allocate a new track's SSRC(s) distinct from every SSRC
/// already in use — a shared SSRC would collide the per-SSRC SRTP context (ROC/replay is keyed by SSRC). The
/// audio SSRC is a fixed member; each video track contributes its primary/per-encoding and RTX SSRC(s), added
/// when the track is built and released when it is deactivated. All operations are serialised by an internal
/// lock, so the session can seed, extend, prune, and snapshot it across its own track-mutation gate safely.
/// </summary>
internal sealed class BundledOutboundSsrcTracker
{
    private readonly object _gate = new();
    private readonly uint _audioSsrc;
    private readonly Dictionary<string, uint[]> _videoSsrcsByMid = new(StringComparer.Ordinal);

    /// <summary>Creates a tracker whose fixed member is the bundle's <paramref name="audioSsrc"/>.</summary>
    public BundledOutboundSsrcTracker(uint audioSsrc) => _audioSsrc = audioSsrc;

    /// <summary>Records a video track's SSRCs as live under its <paramref name="mid"/> (replacing any prior entry).</summary>
    public void Add(string mid, BundledTrackConfig video)
    {
        lock (_gate)
            _videoSsrcsByMid[mid] = CollectTrackSsrcs(video);
    }

    /// <summary>Releases the SSRCs of the video track under <paramref name="mid"/> (no-op when absent, idempotent).</summary>
    public void Remove(string mid)
    {
        lock (_gate)
            _videoSsrcsByMid.Remove(mid);
    }

    /// <summary>
    /// A snapshot of every outbound SSRC live right now: the audio SSRC plus each active video track's
    /// primary/per-encoding and RTX SSRC(s). Taken under the internal lock, so it is a consistent point between
    /// live add/remove mutations.
    /// </summary>
    public IReadOnlySet<uint> Snapshot()
    {
        lock (_gate)
        {
            var ssrcs = new HashSet<uint> { _audioSsrc };
            foreach (var trackSsrcs in _videoSsrcsByMid.Values)
                foreach (var ssrc in trackSsrcs)
                    ssrcs.Add(ssrc);
            return ssrcs;
        }
    }

    // Every outbound SSRC a video track config contributes (RFC 3550 §8.1): its primary SSRC, each simulcast
    // encoding's SSRC (RFC 8853), and the RTX repair SSRC (RFC 4588 §4) when negotiated.
    private static uint[] CollectTrackSsrcs(BundledTrackConfig video)
    {
        var ssrcs = new List<uint> { video.Ssrc };
        foreach (var encoding in video.Encodings)
            ssrcs.Add(encoding.Ssrc);
        if (video.RtxSsrc is { } rtx)
            ssrcs.Add(rtx);
        return ssrcs.ToArray();
    }
}
