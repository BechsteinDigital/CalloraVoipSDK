namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Projects the peer's inbound media onto the W3C per-track model. A track is materialised — and its
/// track-received callback raised exactly once — either when the remote description is applied (the W3C
/// <c>ontrack</c> point) or, as a fallback, on the first frame of that kind. Materialising up front lets a
/// handler subscribe to <see cref="RemoteTrack.FrameReceived"/> before any media arrives.
/// </summary>
/// <remarks>
/// One audio track (a single remote audio m-line is the current scope) plus N video tracks keyed by MID
/// (P2c: several remote cameras/screen-shares stay separable). A video track without a MID (a legacy 1+1
/// remote) is keyed under the empty string, so the single-video path materialises exactly one track.
/// <para>
/// Precondition: inbound frames are delivered <em>serially</em> — the peer's transport dispatches every
/// <c>AudioReceived</c>/<c>VideoFrameReceived</c> from a single receive loop. The lock guarantees exactly
/// one track per key and exactly one callback under any interleaving. Once tracks are materialised from the
/// remote description, frame delivery simply routes to the existing track.
/// </para>
/// </remarks>
internal sealed class RemoteTrackSet
{
    private readonly object _sync = new();
    private readonly Action<RemoteTrack> _onTrackReceived;
    private RemoteTrack? _audio;
    // Video tracks keyed by MID (empty string for a MID-less legacy 1+1 remote), so N remote video m-lines
    // materialise N distinct tracks and each inbound frame routes to its own track.
    private readonly Dictionary<string, RemoteTrack> _video = new(StringComparer.Ordinal);

    public RemoteTrackSet(Action<RemoteTrack> onTrackReceived)
    {
        ArgumentNullException.ThrowIfNull(onTrackReceived);
        _onTrackReceived = onTrackReceived;
    }

    /// <summary>Materialises the audio track (raising the callback once) without delivering a frame.</summary>
    public RemoteTrack EnsureAudioTrack(string? streamId, string? trackId)
    {
        RemoteTrack? created = null;
        RemoteTrack track;
        lock (_sync)
        {
            if (_audio is null)
            {
                _audio = new RemoteTrack(TrackKind.Audio, streamId, trackId, mid: null);
                created = _audio;
            }
            track = _audio;
        }
        if (created is not null) _onTrackReceived(created);
        return track;
    }

    /// <summary>
    /// Materialises the video track for <paramref name="mid"/> (raising the callback once) without delivering a
    /// frame. A null MID keys the single legacy video track (the 1+1 path), so it stays one track.
    /// </summary>
    public RemoteTrack EnsureVideoTrack(string? mid, string? streamId, string? trackId)
    {
        var key = mid ?? string.Empty;
        RemoteTrack? created = null;
        RemoteTrack track;
        lock (_sync)
        {
            if (!_video.TryGetValue(key, out var existing))
            {
                existing = new RemoteTrack(TrackKind.Video, streamId, trackId, mid);
                _video[key] = existing;
                created = existing;
            }
            track = existing;
        }
        if (created is not null) _onTrackReceived(created);
        return track;
    }

    /// <summary>Delivers one inbound audio frame, materialising the audio track first if not already present.</summary>
    public void DeliverAudioFrame(string? streamId, string? trackId, EncodedFrame frame)
        => EnsureAudioTrack(streamId, trackId).RaiseFrame(frame);

    /// <summary>
    /// Delivers one inbound video frame on the <paramref name="mid"/> track (P2c), materialising it first if not
    /// already present. A null MID targets the single legacy video track (the 1+1 path).
    /// </summary>
    public void DeliverVideoFrame(string? mid, string? streamId, string? trackId, EncodedFrame frame)
        => EnsureVideoTrack(mid, streamId, trackId).RaiseFrame(frame);
}
