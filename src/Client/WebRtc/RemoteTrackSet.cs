namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Projects the peer's flat inbound audio/video callbacks onto the W3C per-track model: the first frame of
/// each kind materialises its <see cref="RemoteTrack"/> and raises the track-received callback once, before
/// that first frame is delivered on the track — so a handler that subscribes to
/// <see cref="RemoteTrack.FrameReceived"/> synchronously still catches every frame.
/// </summary>
/// <remarks>
/// Precondition: inbound frames are delivered <em>serially</em> — the peer's transport dispatches every
/// <c>AudioReceived</c>/<c>VideoFrameReceived</c> from a single receive loop. The lock guarantees exactly
/// one track per kind and exactly one callback under any interleaving, but the "first frame after the
/// callback returns" ordering only holds because deliveries are not concurrent: a hypothetical second
/// concurrent first-frame could raise before the materialising caller's subscriber attaches. Any future
/// multi-threaded inbound path must preserve serial delivery per peer or revisit this.
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

    /// <summary>Delivers one inbound audio frame, materialising the audio track on first use.</summary>
    public void DeliverAudioFrame(string? streamId, string? trackId, EncodedFrame frame)
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
        track.RaiseFrame(frame);
    }

    /// <summary>Delivers one inbound video frame, materialising the video track on first use.</summary>
    public void DeliverVideoFrame(string? streamId, string? trackId, EncodedFrame frame)
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
        track.RaiseFrame(frame);
    }
}
