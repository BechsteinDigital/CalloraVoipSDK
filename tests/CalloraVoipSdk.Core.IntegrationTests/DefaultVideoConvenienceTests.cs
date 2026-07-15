using CalloraVoipSdk.Core.Application.Convenience;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
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
/// Pins the default-video convenience path (public Video API, step b — sub-slice 2a): when an
/// application-supplied <see cref="IVideoDevice"/> is registered, <c>AttachDefaultVideoAsync</c> wires a
/// video receiver/sender to the call and hands them to the device on connect; without a device it fails
/// closed. Mirrors the default-audio convenience. Transport-only — the device owns the codec.
/// </summary>
public sealed class DefaultVideoConvenienceTests : IDisposable
{
    private readonly MediaManager _media = new();
    private readonly SipCoreCallChannel _channel;
    private readonly Call _call;

    public DefaultVideoConvenienceTests()
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

    private SdkConvenienceOrchestrator BuildOrchestrator(IVideoDevice? videoDevice) =>
        new(
            new PhoneLineManager(_ => throw new NotSupportedException("lines are not exercised here")),
            _media,
            new NoopAudioDevice(),
            NullLoggerFactory.Instance,
            videoDevice);

    [Fact]
    public async Task Attach_without_a_registered_device_fails_closed()
    {
        using var orchestrator = BuildOrchestrator(videoDevice: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.AttachDefaultVideoAsync(_call, CancellationToken.None));
        Assert.Contains("video codec device", ex.Message);
    }

    [Fact]
    public async Task Attach_connects_the_device_and_routes_inbound_frames_when_connected()
    {
        var device = new RecordingVideoDevice();
        using var orchestrator = BuildOrchestrator(device);
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);

        await orchestrator.AttachDefaultVideoAsync(_call, CancellationToken.None);

        Assert.True(device.Connected);
        Assert.Equal("VP8", device.Parameters!.CodecName); // default until negotiated params are surfaced

        _channel.DeliverInboundVideoFrame(new CallVideoFrame([1, 2, 3], PayloadType: 96, RtpTimestamp: 9000, IsKeyFrame: true));
        var frame = Assert.Single(device.Received);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload.ToArray());
        Assert.True(frame.IsKeyFrame);
    }

    [Fact]
    public async Task Attach_defers_connect_until_the_call_reaches_connected()
    {
        var device = new RecordingVideoDevice();
        using var orchestrator = BuildOrchestrator(device);

        await orchestrator.AttachDefaultVideoAsync(_call, CancellationToken.None); // call still not connected
        Assert.False(device.Connected);

        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);

        Assert.True(device.Connected); // connected on the state transition
    }

    [Fact]
    public async Task Detach_disconnects_the_device()
    {
        var device = new RecordingVideoDevice();
        using var orchestrator = BuildOrchestrator(device);
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);
        await orchestrator.AttachDefaultVideoAsync(_call, CancellationToken.None);
        Assert.True(device.Connected);

        await orchestrator.DetachDefaultVideoAsync(_call, CancellationToken.None);

        Assert.False(device.Connected);
        Assert.Equal(1, device.DisconnectCount);
    }

    [Fact]
    public async Task Terminated_call_disposes_the_attachment_and_disconnects()
    {
        var device = new RecordingVideoDevice();
        using var orchestrator = BuildOrchestrator(device);
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);
        await orchestrator.AttachDefaultVideoAsync(_call, CancellationToken.None);

        _call.TransitionTo(CallState.Terminated);

        Assert.False(device.Connected);
        Assert.Equal(1, device.DisconnectCount);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class RecordingVideoDevice : IVideoDevice
    {
        private IVideoReceiver? _receiver;

        public string Name => "recording-video-device";
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public bool Connected => ConnectCount > DisconnectCount;
        public VideoConnectionParameters? Parameters { get; private set; }
        public List<VideoFrame> Received { get; } = [];

        public void Connect(IVideoReceiver receiver, IVideoSender sender, VideoConnectionParameters parameters)
        {
            _receiver = receiver;
            Parameters = parameters;
            ConnectCount++;
            receiver.FrameReceived += OnFrameReceived;
        }

        public void Disconnect()
        {
            if (_receiver is not null)
                _receiver.FrameReceived -= OnFrameReceived;
            _receiver = null;
            DisconnectCount++;
        }

        private void OnFrameReceived(object? sender, VideoFrameReceivedEventArgs e) => Received.Add(e.Frame);
    }

    private sealed class NoopAudioDevice : IAudioDevice
    {
        public string Name => "noop-audio-device";
        public void Connect(IMediaReceiver receiver, IMediaSender sender, AudioConnectionParameters parameters) { }
        public void Disconnect() { }
    }
}
