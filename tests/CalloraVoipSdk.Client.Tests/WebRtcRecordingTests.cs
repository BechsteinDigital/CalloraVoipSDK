using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The WebRTC recording framework (Track 1, slice 1): <see cref="RecordingTap"/> maps a peer's media-tap
/// callbacks onto an <see cref="IEncodedMediaSink"/>, and <see cref="WebRtcRecorder"/> wires it to a peer
/// via <see cref="IPeerConnection.AttachMediaTap"/> and completes the sink on stop.
/// </summary>
public sealed class WebRtcRecordingTests
{
    [Fact]
    public void The_tap_maps_audio_and_video_to_recorded_frames()
    {
        var sink = new CollectingSink();
        var tap = new RecordingTap(sink);

        tap.OnAudio(MediaDirection.Inbound, new byte[] { 1, 2 });
        tap.OnVideo(MediaDirection.Outbound, new byte[] { 3 }, rtpTimestamp: 90000, isKeyFrame: true);

        Assert.Equal(2, tap.FrameCount);
        Assert.Equal(2, sink.Frames.Count);

        Assert.Equal(TrackKind.Audio, sink.Frames[0].Kind);
        Assert.Equal(MediaDirection.Inbound, sink.Frames[0].Direction);
        Assert.Equal(new byte[] { 1, 2 }, sink.Frames[0].Payload.ToArray());
        Assert.Null(sink.Frames[0].RtpTimestamp);

        Assert.Equal(TrackKind.Video, sink.Frames[1].Kind);
        Assert.Equal(MediaDirection.Outbound, sink.Frames[1].Direction);
        Assert.Equal(90000u, sink.Frames[1].RtpTimestamp);
        Assert.True(sink.Frames[1].IsKeyFrame);
    }

    [Fact]
    public async Task Recorder_streams_a_peers_frames_to_the_sink_until_stopped()
    {
        var peer = new RecordingFakePeer();
        var sink = new CollectingSink();
        var recording = new WebRtcRecorder().Start(peer, sink);

        peer.PushAudio(MediaDirection.Inbound, new byte[] { 1 });
        peer.PushVideo(MediaDirection.Inbound, new byte[] { 2 }, 100, isKeyFrame: false);

        Assert.Equal(2, recording.FrameCount);
        Assert.Equal(2, sink.Frames.Count);
        Assert.Equal(0, sink.CompletedCount);

        await recording.StopAsync();

        Assert.Equal(1, sink.CompletedCount);
        Assert.True(peer.TapDetached);

        peer.PushAudio(MediaDirection.Inbound, new byte[] { 3 });   // after stop: nothing more is captured
        Assert.Equal(2, sink.Frames.Count);
    }

    [Fact]
    public async Task Stopping_twice_completes_the_sink_only_once()
    {
        var peer = new RecordingFakePeer();
        var sink = new CollectingSink();
        var recording = new WebRtcRecorder().Start(peer, sink);

        await recording.StopAsync();
        await recording.StopAsync();
        await recording.DisposeAsync();

        Assert.Equal(1, sink.CompletedCount);
    }

    private sealed class CollectingSink : IEncodedMediaSink
    {
        public List<RecordedFrame> Frames { get; } = [];
        public int CompletedCount { get; private set; }

        public void Write(in RecordedFrame frame)
        {
            // Copy the payload — it is only valid for the duration of this call.
            Frames.Add(new RecordedFrame(frame.Kind, frame.Direction, frame.Payload.ToArray(), frame.RtpTimestamp, frame.IsKeyFrame));
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            CompletedCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingFakePeer : IPeerConnection
    {
        private IMediaTap? _tap;
        public bool TapDetached { get; private set; }

        public void PushAudio(MediaDirection direction, byte[] payload) => _tap?.OnAudio(direction, payload);
        public void PushVideo(MediaDirection direction, byte[] frame, uint? ts, bool isKeyFrame) => _tap?.OnVideo(direction, frame, ts, isKeyFrame);

        public IDisposable AttachMediaTap(IMediaTap tap)
        {
            _tap = tap;
            return new Detacher(this);
        }

        public WebRtcStats GetStats() => new() { ConnectionState = State };

        public PeerConnectionState State => PeerConnectionState.Connected;
        public string? LocalDescription => null;
        public IPEndPoint? LocalMediaEndPoint => null;
        public event EventHandler<PeerConnectionState>? ConnectionStateChanged { add { } remove { } }
        public event EventHandler<RemoteTrack>? TrackReceived { add { } remove { } }
        public string CreateOffer() => string.Empty;
        public Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Detacher(RecordingFakePeer peer) : IDisposable
        {
            public void Dispose()
            {
                peer._tap = null;
                peer.TapDetached = true;
            }
        }
    }
}
