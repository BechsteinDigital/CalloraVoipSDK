namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Owns the tracks a consumer adds to a <see cref="WebRtcPeerConnection"/> at runtime via the public
/// <c>AddAudioTrack</c>/<c>AddVideoTrack</c> surface (4.7.0 N-audio / P2c N-video), extracted from the peer to
/// keep it under the file-size limit. Each added track carries a stable per-track <c>a=msid</c> track id and a
/// numeric MID derived from its m-line index (RFC 8843): the primary audio is MID <c>0</c>, then the added-audio
/// m-lines, then the config-time primary video (if any), then the added-video m-lines. That order is the single
/// source of truth for both the assigned MIDs and the offer's <see cref="WebRtcSdpOptionsBuilder"/> track order,
/// so the two never drift.
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
    private readonly List<(WebRtcAddedAudioTrack Track, string TrackId)> _audio = [];
    private readonly List<(WebRtcAddedVideoTrack Track, string TrackId)> _video = [];

    /// <summary>Creates the set for a peer whose config offers <paramref name="primaryVideoCount"/> primary video m-lines (0 or 1).</summary>
    public WebRtcAddedTrackSet(int primaryVideoCount) => _primaryVideoCount = primaryVideoCount;

    /// <summary>
    /// Records an added audio track and returns its numeric MID. Added-audio m-lines follow the primary audio
    /// (MID 0) directly, so the MID is <c>1 + its position</c> in the added-audio list (RFC 8843).
    /// </summary>
    public string AddAudio(WebRtcAddedAudioTrack track)
    {
        lock (_gate)
        {
            _audio.Add((track, Guid.NewGuid().ToString("N")));
            return _audio.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Records an added video track and returns its numeric MID. Video m-lines follow the primary audio, the
    /// added-audio m-lines, and the config primary video, so the MID is
    /// <c>1 (primary audio) + added-audio-count + primary-video-count + its position</c> among the added videos.
    /// </summary>
    public string AddVideo(WebRtcAddedVideoTrack track)
    {
        lock (_gate)
        {
            _video.Add((track, Guid.NewGuid().ToString("N")));
            var index = 1 + _audio.Count + _primaryVideoCount + (_video.Count - 1);
            return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A point-in-time snapshot of the added audio tracks in insertion order (for the offer builder / diff).</summary>
    public (WebRtcAddedAudioTrack Track, string TrackId)[] SnapshotAudio()
    {
        lock (_gate) return _audio.ToArray();
    }

    /// <summary>A point-in-time snapshot of the added video tracks in insertion order (for the offer builder / diff).</summary>
    public (WebRtcAddedVideoTrack Track, string TrackId)[] SnapshotVideo()
    {
        lock (_gate) return _video.ToArray();
    }
}
