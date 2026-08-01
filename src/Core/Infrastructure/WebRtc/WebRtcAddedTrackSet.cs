namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Owns the tracks a consumer adds to a <see cref="WebRtcPeerConnection"/> at runtime via the public
/// <c>AddAudioTrack</c>/<c>AddVideoTrack</c> surface (4.7.0 N-audio / P2c N-video), extracted from the peer to
/// keep it under the file-size limit. Each added track carries a stable per-track <c>a=msid</c> track id and a
/// stable, append-only numeric MID: the primary audio/video MIDs are reserved from the first offer and every
/// runtime track is appended in API call order (RFC 8829 — an existing m-line never moves or changes MID). The
/// MID is independent of the track's kind, so a video added before an audio can never collide with it. The call
/// order is the single source of truth for both assigned MIDs and the <see cref="WebRtcSdpOptionsBuilder"/>
/// offer, so the two never drift.
/// </summary>
/// <remarks>
/// Thread-safe by its own lock: the peer no longer holds its <c>_sync</c> across these calls, because the added
/// lists interact with no other peer state except the immutable primary-video count captured at construction.
/// A track added mid-call is pending until the next offer/answer cycle applies the diff to the live session
/// (RFC 8829 renegotiation); the set only records identity and hands out stable MIDs.
/// </remarks>
internal sealed class WebRtcAddedTrackSet
{
    private readonly object _gate = new();
    private readonly int _primaryVideoCount;
    private readonly List<(WebRtcAddedAudioTrack Track, string TrackId, int Order)> _audio = [];
    private readonly List<(WebRtcAddedVideoTrack Track, string TrackId, int Order)> _video = [];
    private int _nextOrder;

    /// <summary>
    /// Creates the set for a peer whose config offers <paramref name="primaryVideoCount"/> primary video
    /// m-lines (0 or 1). Every runtime track is appended after those primary m-lines in the exact API call
    /// order and keeps its MID for the session's lifetime (RFC 8829).
    /// </summary>
    public WebRtcAddedTrackSet(int primaryVideoCount)
    {
        _primaryVideoCount = primaryVideoCount;
    }

    /// <summary>
    /// Records an added audio track and returns its stable append-only numeric MID (see <see cref="AppendedMid"/>).
    /// </summary>
    public string AddAudio(WebRtcAddedAudioTrack track)
    {
        lock (_gate)
        {
            var order = _nextOrder++;
            _audio.Add((track, Guid.NewGuid().ToString("N"), order));
            return AppendedMid(order);
        }
    }

    /// <summary>
    /// Records an added video track and returns its stable append-only numeric MID (see <see cref="AppendedMid"/>).
    /// </summary>
    public string AddVideo(WebRtcAddedVideoTrack track)
    {
        lock (_gate)
        {
            var order = _nextOrder++;
            _video.Add((track, Guid.NewGuid().ToString("N"), order));
            return AppendedMid(order);
        }
    }

    // The MID of the runtime track added at the given global call order: primary audio (MID 0), the primary
    // video(s), then every appended track by call order (RFC 8829 append-only). Independent of the track kind,
    // so mixing audio/video add order can never produce a duplicate or shifting MID (the pre-4.7.2 grouped
    // layout could — a video added before an audio drifted onto the audio's MID).
    private string AppendedMid(int order) =>
        (1 + _primaryVideoCount + order).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A point-in-time snapshot of the added audio tracks in insertion order (for the offer builder / diff).</summary>
    public (WebRtcAddedAudioTrack Track, string TrackId, int Order)[] SnapshotAudio()
    {
        lock (_gate) return _audio.ToArray();
    }

    /// <summary>A point-in-time snapshot of the added video tracks in insertion order (for the offer builder / diff).</summary>
    public (WebRtcAddedVideoTrack Track, string TrackId, int Order)[] SnapshotVideo()
    {
        lock (_gate) return _video.ToArray();
    }
}
