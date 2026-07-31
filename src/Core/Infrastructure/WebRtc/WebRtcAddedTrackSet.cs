namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Owns the tracks a consumer adds to a <see cref="WebRtcPeerConnection"/> at runtime via the public
/// <c>AddAudioTrack</c>/<c>AddVideoTrack</c> surface (4.7.0 N-audio / P2c N-video), extracted from the peer to
/// keep it under the file-size limit. Each added track carries a stable per-track <c>a=msid</c> track id and a
/// numeric MID derived from its m-line index (RFC 8843). Compatibility mode retains the historic grouped order
/// (primary audio, added audio, primary video, added video). Stable mode reserves the primary audio/video MIDs
/// from the first offer and appends every added track in API call order. The selected order is the single source
/// of truth for both assigned MIDs and the <see cref="WebRtcSdpOptionsBuilder"/> offer, so the two never drift.
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
    private readonly bool _useStableNumericMediaIds;
    private readonly List<(WebRtcAddedAudioTrack Track, string TrackId, int Order)> _audio = [];
    private readonly List<(WebRtcAddedVideoTrack Track, string TrackId, int Order)> _video = [];
    private int _nextOrder;

    /// <summary>
    /// Creates the set for a peer whose config offers <paramref name="primaryVideoCount"/> primary video
    /// m-lines (0 or 1). Stable numeric mode appends every runtime track after those primary m-lines in the
    /// exact API call order; compatibility mode retains the historic audio-before-video indexing.
    /// </summary>
    public WebRtcAddedTrackSet(int primaryVideoCount, bool useStableNumericMediaIds = false)
    {
        _primaryVideoCount = primaryVideoCount;
        _useStableNumericMediaIds = useStableNumericMediaIds;
    }

    /// <summary>
    /// Records an added audio track and returns its numeric MID. Compatibility mode places added audio directly
    /// after primary audio; stable mode appends it after every primary m-line and earlier runtime track.
    /// </summary>
    public string AddAudio(WebRtcAddedAudioTrack track)
    {
        lock (_gate)
        {
            var order = _nextOrder++;
            _audio.Add((track, Guid.NewGuid().ToString("N"), order));
            var mid = _useStableNumericMediaIds
                ? 1 + _primaryVideoCount + order
                : _audio.Count;
            return mid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Records an added video track and returns its numeric MID. Compatibility mode groups it after primary
    /// audio, added audio, and primary video; stable mode appends it after every primary m-line and earlier
    /// runtime track.
    /// </summary>
    public string AddVideo(WebRtcAddedVideoTrack track)
    {
        lock (_gate)
        {
            var order = _nextOrder++;
            _video.Add((track, Guid.NewGuid().ToString("N"), order));
            var index = _useStableNumericMediaIds
                ? 1 + _primaryVideoCount + order
                : 1 + _audio.Count + _primaryVideoCount + (_video.Count - 1);
            return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

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
