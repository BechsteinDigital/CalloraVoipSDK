using System.Net;
using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the orchestrator's video wiring (public Video API, step a — sub-slice 2): when the media
/// session negotiates a video sub-stream, inbound reassembled frames reach the call channel and the
/// channel's outbound video send routes to the stream; teardown unsubscribes and clears the delegate.
/// Audio-only legs stay untouched.
/// </summary>
public sealed class CallMediaOrchestratorVideoWiringTests
{
    private static CallMediaParameters VideoParams() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        RtcpMux = true, // RTCP shares the RTP port → the monitor binds loopback:0 (OS-assigned, no clash)
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    private static (CallMediaOrchestrator Orchestrator, RecordingCallChannel Channel, Call Call) Build(
        IVideoMediaStream? video)
    {
        var channel = new RecordingCallChannel();
        var orchestrator = new CallMediaOrchestrator(
            new FakeSessionFactory(new FakeVideoSession(video)),
            NullLoggerFactory.Instance,
            new RtcpPacketCodec());
        var call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            channel, new FakePhoneLine(), NullLogger<Call>.Instance);
        orchestrator.AttachCall(call, channel);
        return (orchestrator, channel, call);
    }

    [Fact]
    public void Inbound_stream_frame_reaches_the_channel_as_a_video_frame()
    {
        var stream = new RecordingVideoStream { PayloadType = 96 };
        var (orchestrator, channel, _) = Build(stream);
        using var _o = orchestrator;
        channel.RaiseMediaNegotiated(VideoParams());

        stream.RaiseFrameReceived([1, 2, 3], rtpTimestamp: 9000);

        var frame = Assert.Single(channel.InboundVideo);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload);
        Assert.Equal(96, frame.PayloadType);        // fixed by the negotiated video codec
        Assert.Equal(9000u, frame.RtpTimestamp);
        Assert.False(frame.IsKeyFrame);             // delta frame → depacketiser classified it as non-key
    }

    [Fact]
    public void Inbound_keyframe_flag_is_forwarded_to_the_channel()
    {
        var stream = new RecordingVideoStream { PayloadType = 96 };
        var (orchestrator, channel, _) = Build(stream);
        using var _o = orchestrator;
        channel.RaiseMediaNegotiated(VideoParams());

        stream.RaiseFrameReceived([1, 2, 3], rtpTimestamp: 9000, keyFrame: true);

        var frame = Assert.Single(channel.InboundVideo);
        Assert.True(frame.IsKeyFrame); // depacketiser's keyframe classification reaches the call channel
    }

    [Fact]
    public void Keyframe_request_from_stream_reaches_the_call()
    {
        var stream = new RecordingVideoStream { PayloadType = 96 };
        var (orchestrator, channel, call) = Build(stream);
        using var _o = orchestrator;
        var requests = 0;
        call.VideoKeyFrameRequested += () => requests++;
        channel.RaiseMediaNegotiated(VideoParams());

        stream.RaiseKeyFrameRequested();

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Teardown_stops_keyframe_requests_to_the_call()
    {
        var stream = new RecordingVideoStream { PayloadType = 96 };
        var (orchestrator, channel, call) = Build(stream);
        using var _o = orchestrator;
        var requests = 0;
        call.VideoKeyFrameRequested += () => requests++;
        channel.RaiseMediaNegotiated(VideoParams());

        orchestrator.OnCallStateChanged(
            this, new CallStateChangedEventArgs(CallState.Connected, CallState.Terminated, call));
        await WaitUntilAsync(() => channel.VideoSendDelegateClearedCount > 0);

        stream.RaiseKeyFrameRequested(); // unsubscribed on teardown → must be ignored

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Channel_video_send_delegate_routes_to_the_stream()
    {
        var stream = new RecordingVideoStream { PayloadType = 96 };
        var (orchestrator, channel, _) = Build(stream);
        using var _o = orchestrator;
        channel.RaiseMediaNegotiated(VideoParams());

        Assert.NotNull(channel.VideoSendDelegate);
        await channel.VideoSendDelegate!(
            new CallVideoFrame([9, 8, 7], PayloadType: 96, RtpTimestamp: 12345, IsKeyFrame: true), default);

        var sent = Assert.Single(stream.Sent);
        Assert.Equal(new byte[] { 9, 8, 7 }, sent.Frame);
        Assert.Equal(12345u, sent.Timestamp);
    }

    [Fact]
    public void Audio_only_session_wires_no_video_send_delegate()
    {
        var (orchestrator, channel, _) = Build(video: null);
        using var _o = orchestrator;

        channel.RaiseMediaNegotiated(VideoParams());

        Assert.Null(channel.VideoSendDelegate);
        Assert.Equal(0, channel.VideoSendDelegateSetCount);
    }

    [Fact]
    public async Task Teardown_unsubscribes_the_stream_and_clears_the_send_delegate()
    {
        var stream = new RecordingVideoStream { PayloadType = 96 };
        var (orchestrator, channel, call) = Build(stream);
        using var _o = orchestrator;
        channel.RaiseMediaNegotiated(VideoParams());
        Assert.NotNull(channel.VideoSendDelegate);

        orchestrator.OnCallStateChanged(
            this, new CallStateChangedEventArgs(CallState.Connected, CallState.Terminated, call));

        // TeardownMediaAsync is fire-and-forget; wait for the synchronous unwire inside it to land.
        await WaitUntilAsync(() => channel.VideoSendDelegateClearedCount > 0);

        channel.InboundVideo.Clear();
        stream.RaiseFrameReceived([1], 1);
        Assert.Empty(channel.InboundVideo); // handler was unsubscribed on teardown
    }

    [Fact]
    public void Congestion_recommendation_is_primed_on_wiring_and_refreshed_on_each_report()
    {
        var stream = new RecordingVideoStream
        {
            PayloadType = 96,
            RecommendedBitrateBps = 800_000,
            NetworkQuality = NetworkQuality.Good,
        };
        var (orchestrator, channel, call) = Build(stream);
        using var _o = orchestrator;
        var changes = 0;
        call.VideoCongestionChanged += () => changes++;

        channel.RaiseMediaNegotiated(VideoParams());

        Assert.Equal(800_000, call.RecommendedVideoBitrateBps); // primed on wiring
        Assert.Equal(NetworkQuality.Good, call.VideoNetworkQuality);

        stream.RecommendedBitrateBps = 400_000;
        stream.NetworkQuality = NetworkQuality.Poor;
        stream.RaiseCongestionUpdated();

        Assert.Equal(400_000, call.RecommendedVideoBitrateBps);
        Assert.Equal(NetworkQuality.Poor, call.VideoNetworkQuality);
        Assert.True(changes >= 1);
    }

    [Fact]
    public async Task Teardown_stops_congestion_updates_to_the_call()
    {
        var stream = new RecordingVideoStream
        {
            PayloadType = 96,
            RecommendedBitrateBps = 800_000,
            NetworkQuality = NetworkQuality.Good,
        };
        var (orchestrator, channel, call) = Build(stream);
        using var _o = orchestrator;
        channel.RaiseMediaNegotiated(VideoParams());

        orchestrator.OnCallStateChanged(
            this, new CallStateChangedEventArgs(CallState.Connected, CallState.Terminated, call));
        await WaitUntilAsync(() => channel.VideoSendDelegateClearedCount > 0);

        stream.RecommendedBitrateBps = 111_000;
        stream.RaiseCongestionUpdated(); // unsubscribed on teardown → must be ignored

        Assert.Equal(800_000, call.RecommendedVideoBitrateBps); // unchanged from the initial prime
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "condition was not met within the timeout");
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class RecordingVideoStream : IVideoMediaStream
    {
        public string CodecName => "VP8";
        public int PayloadType { get; init; } = 96;
        public List<(byte[] Frame, uint Timestamp)> Sent { get; } = [];
        public long? RecommendedBitrateBps { get; set; }
        public NetworkQuality? NetworkQuality { get; set; }

        public event Action<byte[], uint, bool>? FrameReceived;
        public event Action? KeyFrameRequested;
        public event Action? CongestionUpdated;

        public Task SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken ct = default)
        {
            Sent.Add((encodedFrame.ToArray(), rtpTimestamp));
            return Task.CompletedTask;
        }

        public void RaiseFrameReceived(byte[] frame, uint rtpTimestamp, bool keyFrame = false) =>
            FrameReceived?.Invoke(frame, rtpTimestamp, keyFrame);
        public void RaiseKeyFrameRequested() => KeyFrameRequested?.Invoke();
        public void RaiseCongestionUpdated() => CongestionUpdated?.Invoke();
    }

    private sealed class FakeVideoSession(IVideoMediaStream? video) : ICallMediaSession
    {
        public IVideoMediaStream? Video => video;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendFrameAsync(CallAudioFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken ct = default) => Task.CompletedTask;
        public void UpdateRoundTripTimeHint(TimeSpan roundTripTime) { }
        public CallMediaRuntimeMetrics GetRuntimeMetricsSnapshot() =>
            new(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public CallMediaRtpSnapshot GetRtpSnapshot() =>
            new(DateTimeOffset.UtcNow, 0u, null, 0u, 0u, 0u, false, 0u, 0u, 0, 0, 0u, 0u, 0, 0, 0);
        public Task SendRtcpMuxDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS0067 // audio-path events are unused by these video-wiring tests
        public event Action<CallAudioFrame>? FrameReceived;
        public event Action<byte, int>? DtmfReceived;
        public event Action<CallMediaRuntimeMetrics>? RuntimeMetricsUpdated;
        public event Action<byte[]>? RtcpMuxDatagramReceived;
#pragma warning restore CS0067

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSessionFactory(ICallMediaSession session) : ICallMediaSessionFactory
    {
        public ICallMediaSession Create(CallMediaParameters parameters) => session;
    }

    private sealed class RecordingCallChannel : ICallChannel
    {
        public List<CallVideoFrame> InboundVideo { get; } = [];
        public Func<CallVideoFrame, CancellationToken, Task>? VideoSendDelegate { get; private set; }
        public int VideoSendDelegateSetCount { get; private set; }
        public int VideoSendDelegateClearedCount { get; private set; }

        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
        public void RaiseMediaNegotiated(CallMediaParameters parameters) =>
            MediaParametersNegotiated?.Invoke(this, parameters);

        public void DeliverInboundVideoFrame(CallVideoFrame frame) => InboundVideo.Add(frame);
        public void SetVideoSendDelegate(Func<CallVideoFrame, CancellationToken, Task>? sendDelegate)
        {
            VideoSendDelegate = sendDelegate;
            if (sendDelegate is null) VideoSendDelegateClearedCount++;
            else VideoSendDelegateSetCount++;
        }

        // Audio / DTMF / callbacks: present for the contract, not exercised here.
        public void DeliverInboundAudioFrame(CallAudioFrame frame) { }
        public void SetAudioSendDelegate(Func<CallAudioFrame, CancellationToken, Task>? sendDelegate) { }
        public void AddAudioFrameListener(Action<CallAudioFrame> onFrame) { }
        public void RemoveAudioFrameListener(Action<CallAudioFrame> onFrame) { }
        public void AddVideoFrameListener(Action<CallVideoFrame> onFrame) { }
        public void RemoveVideoFrameListener(Action<CallVideoFrame> onFrame) { }
        public void BindCallbacks(CallChannelCallbacks callbacks) { }

        // SIP actions: not reached by the media-wiring path.
        public Task AnswerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task HangupAsync() => Task.CompletedTask;
        public Task HoldAsync() => Task.CompletedTask;
        public Task UnholdAsync() => Task.CompletedTask;
        public Task SendDtmfAsync(byte dtmfCode) => Task.CompletedTask;
        public Task RejectAsync(int statusCode, string? reasonPhrase, CancellationToken ct) => Task.CompletedTask;
        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode, CancellationToken ct) => Task.CompletedTask;
        public Task SendInfoAsync(string contentType, string body, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> SendOptionsAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds, string? acceptHeader, string? body, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType, string? body, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> BlindTransferAsync(string targetUri, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> AttendedTransferAsync(ICallChannel target, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
        public Task SendAudioFrameAsync(CallAudioFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendVideoFrameAsync(CallVideoFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
