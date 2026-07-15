using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Video;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the video device/codec port (public Video API, step b — sub-slice 1): an
/// <see cref="IVideoDevice"/> connected to a call's public video tap observes inbound encoded frames
/// and can push outbound ones, receives the negotiated parameters, and detaches on
/// <see cref="IVideoDevice.Disconnect"/>. Transport-only — the port moves encoded bytes; the device
/// owns the codec.
/// </summary>
public sealed class VideoDevicePortContractTests : IDisposable
{
    private readonly MediaManager _media = new();
    private readonly SipCoreCallChannel _channel;
    private readonly Call _call;

    public VideoDevicePortContractTests()
    {
        _channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

        _call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            _channel, new FakePhoneLine(), NullLogger<Call>.Instance);
    }

    public void Dispose() => _channel.Dispose();

    private (RecordingVideoDevice Device, IVideoReceiver Receiver, IVideoSender Sender) Connect(
        VideoConnectionParameters? parameters = null)
    {
        IVideoReceiver receiver = _media.CreateVideoReceiver();
        IVideoSender sender = _media.CreateVideoSender();
        receiver.AttachToCall(_call);
        sender.AttachToCall(_call);

        var device = new RecordingVideoDevice();
        device.Connect(receiver, sender, parameters ?? VideoConnectionParameters.Default);
        return (device, receiver, sender);
    }

    [Fact]
    public void Connected_device_receives_inbound_frames_through_the_tap()
    {
        var (device, receiver, sender) = Connect();
        using var _r = receiver;
        using var _s = sender;

        _channel.DeliverInboundVideoFrame(new CallVideoFrame([1, 2, 3], PayloadType: 96, RtpTimestamp: 9000, IsKeyFrame: true));

        var frame = Assert.Single(device.Received);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload.ToArray());
        Assert.Equal(96, frame.PayloadType);
        Assert.True(frame.IsKeyFrame);
    }

    [Fact]
    public async Task Connected_device_sends_outbound_frames_through_the_tap()
    {
        var sent = new List<CallVideoFrame>();
        _channel.SetVideoSendDelegate((frame, _) =>
        {
            lock (sent) sent.Add(frame);
            return Task.CompletedTask;
        });
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);

        var (device, receiver, sender) = Connect();
        using var _r = receiver;
        using var _s = sender;

        await device.SendAsync(new VideoFrame(new byte[] { 9, 8, 7 }, PayloadType: 96, RtpTimestamp: 12345, IsKeyFrame: true));

        var frame = Assert.Single(sent);
        Assert.Equal(new byte[] { 9, 8, 7 }, frame.Payload);
        Assert.Equal(12345u, frame.RtpTimestamp);
    }

    [Fact]
    public void Negotiated_parameters_reach_the_device()
    {
        var parameters = new VideoConnectionParameters { PayloadType = 100, CodecName = "H264", ClockRate = 90_000 };
        var (device, receiver, sender) = Connect(parameters);
        using var _r = receiver;
        using var _s = sender;

        Assert.Same(parameters, device.Parameters);
        Assert.Equal("H264", device.Parameters!.CodecName);
        Assert.Equal(100, device.Parameters.PayloadType);
    }

    [Fact]
    public void Disconnected_device_stops_receiving_inbound_frames()
    {
        var (device, receiver, sender) = Connect();
        using var _r = receiver;
        using var _s = sender;

        device.Disconnect();
        _channel.DeliverInboundVideoFrame(new CallVideoFrame([1], PayloadType: 96, RtpTimestamp: 1, IsKeyFrame: false));

        Assert.Empty(device.Received);
    }

    [Fact]
    public void Port_default_parameters_are_video_clock_vp8()
    {
        Assert.Equal(90_000, VideoConnectionParameters.Default.ClockRate);
        Assert.Equal("VP8", VideoConnectionParameters.Default.CodecName);
    }

    // ── Test double: a minimal IVideoDevice that records inbound frames and relays outbound ones ──

    private sealed class RecordingVideoDevice : IVideoDevice
    {
        private IVideoReceiver? _receiver;
        private IVideoSender? _sender;

        public string Name => "recording-test-video-device";
        public List<VideoFrame> Received { get; } = [];
        public VideoConnectionParameters? Parameters { get; private set; }

        public void Connect(IVideoReceiver receiver, IVideoSender sender, VideoConnectionParameters parameters)
        {
            _receiver = receiver;
            _sender = sender;
            Parameters = parameters;
            receiver.FrameReceived += OnFrameReceived;
        }

        public void Disconnect()
        {
            if (_receiver is not null)
                _receiver.FrameReceived -= OnFrameReceived;
            _receiver = null;
            _sender = null;
        }

        public Task SendAsync(VideoFrame frame) => _sender?.SendAsync(frame) ?? Task.CompletedTask;

        private void OnFrameReceived(object? sender, VideoFrameReceivedEventArgs e) => Received.Add(e.Frame);
    }
}
