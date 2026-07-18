namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Maps a peer's media-tap callbacks onto a recording sink, tagging each frame with kind and direction and
/// counting captured frames for the session handle. Extracted so the mapping is unit-testable without a
/// live peer.
/// </summary>
internal sealed class RecordingTap : IMediaTap
{
    private readonly IEncodedMediaSink _sink;
    private long _frameCount;

    public RecordingTap(IEncodedMediaSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    public long FrameCount => Interlocked.Read(ref _frameCount);

    public void OnAudio(MediaDirection direction, ReadOnlyMemory<byte> payload)
    {
        _sink.Write(new RecordedFrame(TrackKind.Audio, direction, payload, rtpTimestamp: null, isKeyFrame: false));
        Interlocked.Increment(ref _frameCount);
    }

    public void OnVideo(MediaDirection direction, ReadOnlyMemory<byte> frame, uint? rtpTimestamp, bool isKeyFrame)
    {
        _sink.Write(new RecordedFrame(TrackKind.Video, direction, frame, rtpTimestamp, isKeyFrame));
        Interlocked.Increment(ref _frameCount);
    }
}
