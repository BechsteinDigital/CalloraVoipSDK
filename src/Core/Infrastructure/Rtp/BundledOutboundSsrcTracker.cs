using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Tracks every outbound synchronisation source this bundled media session has issued (RFC 3550 §8.1), keyed
/// by the MID that owns them, so a mid-call renegotiation allocates a new track's SSRC(s) distinct from all of
/// them. The audio SSRC is a fixed member; each video track contributes its primary/per-encoding and RTX
/// SSRC(s), added when the track is built. All operations are serialised by an internal lock, so the session
/// can seed, extend, retire, and snapshot it across its own track-mutation gate safely.
/// <para>
/// A deactivated track's SSRCs are <em>retired, not released</em> (#161 P2-12). The bundle protects every
/// stream with one shared <c>SrtpContext</c> under one DTLS-derived master key, and that context keys its
/// per-SSRC state — the rollover counter and replay window — by SSRC for the lifetime of the key. Handing a
/// retired SSRC to a new track would restart that stream's sequence numbering at a fresh random value under
/// the same key: SRTP derives its keystream from (SSRC, ROC‖SEQ), so the new stream would re-issue
/// index values the retired one already used, and the two ciphertexts would share a keystream. That is a
/// two-time pad, not a collision — the earlier comment here, which claimed the SRTP context was gone with the
/// track, was simply wrong.
/// </para>
/// </summary>
internal sealed class BundledOutboundSsrcTracker
{
    // Retired SSRCs accumulate for the lifetime of the key (the session), one entry per SSRC of every
    // deactivated track. That is bounded by how often the application renegotiates, not by anything a peer
    // controls, and each entry is four bytes — but a session churning through this many tracks is worth
    // surfacing, so the threshold is logged once (K4: observable, never silently forgotten).
    private const int RetiredSsrcLogThreshold = 1024;

    private readonly object _gate = new();
    private readonly uint _audioSsrc;
    private readonly Dictionary<string, uint[]> _videoSsrcsByMid = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _retired = [];
    private readonly ILogger? _logger;
    private bool _loggedRetiredThreshold;

    /// <summary>Creates a tracker whose fixed member is the bundle's <paramref name="audioSsrc"/>.</summary>
    public BundledOutboundSsrcTracker(uint audioSsrc, ILogger? logger = null)
    {
        _audioSsrc = audioSsrc;
        _logger = logger;
    }

    /// <summary>
    /// Records a track's SSRCs under its <paramref name="mid"/>. Any SSRCs previously held under that MID are
    /// retired rather than dropped, so replacing an entry cannot re-open them for allocation either.
    /// </summary>
    public void Add(string mid, BundledTrackConfig video)
    {
        lock (_gate)
        {
            if (_videoSsrcsByMid.TryGetValue(mid, out var previous))
                RetireLocked(previous);
            _videoSsrcsByMid[mid] = CollectTrackSsrcs(video);
        }
    }

    /// <summary>
    /// Retires the SSRCs of the track under <paramref name="mid"/>: the track stops sending, but its SSRCs stay
    /// unavailable for allocation for as long as the SRTP key lives (see the type remarks). No-op when the MID
    /// is absent (idempotent).
    /// </summary>
    public void Remove(string mid)
    {
        lock (_gate)
        {
            if (_videoSsrcsByMid.Remove(mid, out var retired))
                RetireLocked(retired);
        }
    }

    /// <summary>
    /// A snapshot of every outbound SSRC this session has issued under its current key: the audio SSRC, each
    /// active track's primary/per-encoding and RTX SSRC(s), and every SSRC retired with a deactivated track.
    /// This is the set an allocation must avoid — not just what is sending right now. Taken under the internal
    /// lock, so it is a consistent point between live add/remove mutations.
    /// </summary>
    public IReadOnlySet<uint> Snapshot()
    {
        lock (_gate)
        {
            var ssrcs = new HashSet<uint>(_retired) { _audioSsrc };
            foreach (var trackSsrcs in _videoSsrcsByMid.Values)
                foreach (var ssrc in trackSsrcs)
                    ssrcs.Add(ssrc);
            return ssrcs;
        }
    }

    // Moves a track's SSRCs into the retired set. Caller holds _gate.
    private void RetireLocked(uint[] ssrcs)
    {
        foreach (var ssrc in ssrcs)
            _retired.Add(ssrc);

        if (_retired.Count < RetiredSsrcLogThreshold || _loggedRetiredThreshold)
            return;

        _loggedRetiredThreshold = true;
        _logger?.LogInformation(
            "This bundle has retired {Count} outbound SSRCs under one SRTP key; they stay reserved so no stream " +
            "can reuse an index under that key.", _retired.Count);
    }

    // Every outbound SSRC a track config contributes (RFC 3550 §8.1): its primary SSRC, each simulcast
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
