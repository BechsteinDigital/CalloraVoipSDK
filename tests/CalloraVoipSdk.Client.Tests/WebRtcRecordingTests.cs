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
        tap.OnVideo(MediaDirection.Outbound, new byte[] { 3 }, rtpTimestamp: 90000, isKeyFrame: true, rid: "hi");

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
        Assert.Equal("hi", sink.Frames[1].Rid); // the simulcast layer id reaches the recording sink
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

    /// <summary>
    /// #166 P2-5: stopping is one shared operation. A second stop must not report success while the first
    /// flush is still running — it joins that flush and returns only once the sink is actually complete.
    /// </summary>
    [Fact]
    public async Task A_second_stop_joins_the_in_flight_flush_instead_of_reporting_success_early()
    {
        var peer = new RecordingFakePeer();
        var sink = new GatedSink();
        var recording = new WebRtcRecorder().Start(peer, sink);

        var first = recording.StopAsync();
        var second = recording.StopAsync();
        var third = recording.DisposeAsync().AsTask();

        // The flush is parked inside CompleteAsync: no caller may claim the recording is stopped yet.
        await sink.Entered.Task;
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        sink.Release();

        await Task.WhenAll(first, second, third);
        Assert.Equal(1, sink.CompletedCount);
    }

    /// <summary>
    /// #166 P2-5: a failed flush must not latch the handle. Before the fix the stopped flag was set first, so
    /// after a throwing CompleteAsync every later stop/dispose was a permanent no-op and the media was lost.
    /// </summary>
    [Fact]
    public async Task A_failed_stop_stays_retryable()
    {
        var peer = new RecordingFakePeer();
        var sink = new FailingOnceSink();
        var recording = new WebRtcRecorder().Start(peer, sink);

        await Assert.ThrowsAsync<IOException>(() => recording.StopAsync());
        Assert.Equal(0, sink.CompletedCount);

        await recording.StopAsync();     // the retry actually re-runs the flush
        Assert.Equal(1, sink.CompletedCount);

        await recording.StopAsync();     // and is idempotent again from here on
        await recording.DisposeAsync();
        Assert.Equal(1, sink.CompletedCount);
    }

    // A sink whose CompleteAsync parks until released, so a test can observe the window in which the first
    // stop is flushing and a second stop arrives.
    private sealed class GatedSink : IEncodedMediaSink
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CompletedCount { get; private set; }

        public void Write(in RecordedFrame frame)
        {
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await _gate.Task;
            CompletedCount++;
        }

        public void Release() => _gate.TrySetResult();
    }

    // Fails the first flush, succeeds afterwards — the retry path of a failed stop.
    private sealed class FailingOnceSink : IEncodedMediaSink
    {
        private int _attempts;

        public int CompletedCount { get; private set; }

        public void Write(in RecordedFrame frame)
        {
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (++_attempts == 1)
                throw new IOException("flush-boom");

            CompletedCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CollectingSink : IEncodedMediaSink
    {
        public List<RecordedFrame> Frames { get; } = [];
        public int CompletedCount { get; private set; }

        public void Write(in RecordedFrame frame)
        {
            // Copy the payload — it is only valid for the duration of this call.
            Frames.Add(new RecordedFrame(frame.Kind, frame.Direction, frame.Payload.ToArray(), frame.RtpTimestamp, frame.IsKeyFrame, frame.Rid));
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
        public void PushVideo(MediaDirection direction, byte[] frame, uint? ts, bool isKeyFrame, string? rid = null) => _tap?.OnVideo(direction, frame, ts, isKeyFrame, rid);

        public IDisposable AttachMediaTap(IMediaTap tap)
        {
            _tap = tap;
            return new Detacher(this);
        }

        public WebRtcStats GetStats() => new() { ConnectionState = State };

        public PeerConnectionState State => PeerConnectionState.Connected;
        public SignalingState SignalingState => SignalingState.Stable;
        public string? LocalDescription => null;
        public IPEndPoint? LocalMediaEndPoint => null;
        public IReadOnlyList<string> NegotiatedReceiveSimulcastRids => [];
        public event EventHandler<PeerConnectionState>? ConnectionStateChanged { add { } remove { } }
        public event EventHandler<SignalingState>? SignalingStateChanged { add { } remove { } }
        public event EventHandler<RemoteTrack>? TrackReceived { add { } remove { } }
        public event EventHandler<string>? LocalIceCandidateDiscovered { add { } remove { } }
        public event EventHandler<DtmfTone>? DtmfReceived { add { } remove { } }
        public event EventHandler? VideoKeyFrameRequested { add { } remove { } }
        public event EventHandler<KeyFrameRequest>? VideoTrackKeyFrameRequested { add { } remove { } }
        public event EventHandler<BitrateRecommendation>? RecommendedBitrateChanged { add { } remove { } }
        public long? RecommendedOutgoingBitrateBps => null;
        public string CreateOffer() => string.Empty;
        public IVideoTrack AddVideoTrack() => throw new NotSupportedException();
        public IVideoTrack AddVideoTrack(VideoTrackOptions options) => throw new NotSupportedException();
        public IAudioTrack AddAudioTrack() => throw new NotSupportedException();
        public IAudioTrack AddAudioTrack(AudioTrackOptions options) => throw new NotSupportedException();
        public Task AddIceCandidateAsync(string candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task GatherCandidatesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, bool isKeyFrame, CancellationToken cancellationToken = default)
            => SendVideoFrameAsync(encodedFrame, rtpTimestamp, cancellationToken);

        public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, bool isKeyFrame, CancellationToken cancellationToken = default)
            => SendVideoFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken);
        public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask<bool> RequestVideoKeyFrameAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        // Records the MID the per-track key-frame overload was asked for, so a passthrough test can assert the
        // client hands the exact MID down to the peer.
        public string? LastKeyFrameMid { get; private set; }
        public ValueTask<bool> RequestVideoKeyFrameAsync(string mid, CancellationToken cancellationToken = default)
        {
            LastKeyFrameMid = mid;
            return ValueTask.FromResult(true);
        }

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
