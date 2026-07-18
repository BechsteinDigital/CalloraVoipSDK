namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Projects the peer's inbound media onto the W3C per-track model. A track is materialised — and its
/// track-received callback raised exactly once — either when the remote description is applied (the W3C
/// <c>ontrack</c> point) or, as a fallback, on the first frame of that kind. Materialising up front lets a
/// handler subscribe to <see cref="RemoteTrack.FrameReceived"/> before any media arrives.
/// </summary>
/// <remarks>
/// Precondition: inbound frames are delivered <em>serially</em> — the peer's transport dispatches every
/// <c>AudioReceived</c>/<c>VideoFrameReceived</c> from a single receive loop. The lock guarantees exactly
/// one track per kind and exactly one callback under any interleaving. Once tracks are materialised from the
/// remote description, frame delivery simply routes to the existing track.
/// </remarks>
internal sealed class RemoteTrackSet
{
    private readonly object _sync = new();
    private readonly Action<RemoteTrack> _onTrackReceived;
    private RemoteTrack? _audio;
    private RemoteTrack? _video;

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
                _audio = new RemoteTrack(TrackKind.Audio, streamId, trackId);
                created = _audio;
            }
            track = _audio;
        }
        if (created is not null) _onTrackReceived(created);
        return track;
    }

    /// <summary>Materialises the video track (raising the callback once) without delivering a frame.</summary>
    public RemoteTrack EnsureVideoTrack(string? streamId, string? trackId)
    {
        RemoteTrack? created = null;
        RemoteTrack track;
        lock (_sync)
        {
            if (_video is null)
            {
                _video = new RemoteTrack(TrackKind.Video, streamId, trackId);
                created = _video;
            }
            track = _video;
        }
        if (created is not null) _onTrackReceived(created);
        return track;
    }

    /// <summary>Delivers one inbound audio frame, materialising the audio track first if not already present.</summary>
    public void DeliverAudioFrame(string? streamId, string? trackId, EncodedFrame frame)
        => EnsureAudioTrack(streamId, trackId).RaiseFrame(frame);

    /// <summary>Delivers one inbound video frame, materialising the video track first if not already present.</summary>
    public void DeliverVideoFrame(string? streamId, string? trackId, EncodedFrame frame)
        => EnsureVideoTrack(streamId, trackId).RaiseFrame(frame);
}
