using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The inbound track projection (ADR-012 step 4): <see cref="RemoteTrackSet"/> turns the peer's flat
/// audio/video callbacks into the W3C per-track model — one <see cref="RemoteTrack"/> per kind, raised once
/// before its first frame, identified by the remote a=msid.
/// </summary>
public sealed class RemoteTrackSetTests
{
    private static EncodedFrame Audio(params byte[] payload) => new(payload, rtpTimestamp: null, isKeyFrame: false, presentationTimeUsec: null);
    private static EncodedFrame Video(uint ts, bool key, params byte[] payload) => new(payload, ts, key, presentationTimeUsec: null);

    [Fact]
    public void First_frame_materialises_the_track_and_raises_TrackReceived_once()
    {
        var tracks = new List<RemoteTrack>();
        var set = new RemoteTrackSet(tracks.Add);

        set.DeliverAudioFrame("stream-1", "track-a", Audio(1, 2));

        var track = Assert.Single(tracks);
        Assert.Equal(TrackKind.Audio, track.Kind);
        Assert.Equal("stream-1", track.StreamId);
        Assert.Equal("track-a", track.TrackId);
    }

    [Fact]
    public void A_handler_subscribed_in_the_track_received_callback_catches_the_first_frame()
    {
        var frames = new List<EncodedFrame>();
        var set = new RemoteTrackSet(track => track.FrameReceived += (_, f) => frames.Add(f));

        set.DeliverAudioFrame("s", "t", Audio(9));

        var frame = Assert.Single(frames);
        Assert.Equal(new byte[] { 9 }, frame.Payload.ToArray());
        Assert.Null(frame.RtpTimestamp);          // audio carries no RTP timestamp yet (follow-up)
        Assert.False(frame.IsKeyFrame);
        Assert.Null(frame.PresentationTimeUsec);  // RTCP-SR mapping deferred
    }

    [Fact]
    public void Subsequent_frames_reuse_the_same_track_without_re_raising()
    {
        var trackCount = 0;
        var frames = new List<EncodedFrame>();
        var set = new RemoteTrackSet(track => { trackCount++; track.FrameReceived += (_, f) => frames.Add(f); });

        set.DeliverAudioFrame("s", "t", Audio(1));
        set.DeliverAudioFrame("s", "t", Audio(2));
        set.DeliverAudioFrame("s", "t", Audio(3));

        Assert.Equal(1, trackCount);
        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public void Audio_and_video_are_separate_tracks_grouped_by_stream_id()
    {
        var tracks = new List<RemoteTrack>();
        var set = new RemoteTrackSet(tracks.Add);

        set.DeliverAudioFrame("stream-1", "audio-track", Audio(1));
        set.DeliverVideoFrame("stream-1", "video-track", Video(90000, true, 2));

        Assert.Equal(2, tracks.Count);
        Assert.Equal(TrackKind.Audio, tracks[0].Kind);
        Assert.Equal(TrackKind.Video, tracks[1].Kind);
        Assert.Equal(tracks[0].StreamId, tracks[1].StreamId);   // one remote MediaStream — audio/video stay grouped
        Assert.NotEqual(tracks[0].TrackId, tracks[1].TrackId);
    }

    [Fact]
    public void Video_frames_surface_their_rtp_timestamp_and_key_frame_flag()
    {
        EncodedFrame? received = null;
        var set = new RemoteTrackSet(track => track.FrameReceived += (_, f) => received = f);

        set.DeliverVideoFrame("s", "t", Video(123456, key: true, 7, 8));

        Assert.NotNull(received);
        Assert.Equal(123456u, received!.Value.RtpTimestamp);
        Assert.True(received.Value.IsKeyFrame);
    }

    [Fact]
    public void A_null_stream_id_is_carried_through()   // RFC 8830 "-" is normalised to null upstream in the adapter
    {
        var tracks = new List<RemoteTrack>();
        var set = new RemoteTrackSet(tracks.Add);

        set.DeliverAudioFrame(null, null, Audio(1));

        Assert.Null(Assert.Single(tracks).StreamId);
    }

    [Fact]
    public async Task Concurrent_first_frames_materialise_exactly_one_track()
    {
        var trackCount = 0;
        var frameCount = 0;
        var set = new RemoteTrackSet(track =>
        {
            Interlocked.Increment(ref trackCount);
            track.FrameReceived += (_, _) => Interlocked.Increment(ref frameCount);
        });

        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() => set.DeliverAudioFrame("s", "t", Audio(1))));
        await Task.WhenAll(tasks);

        Assert.Equal(1, trackCount);        // the lock admits exactly one materialisation, no torn state
        Assert.True(frameCount >= 1);       // the materialising caller subscribes before delivering its own frame
    }
}
