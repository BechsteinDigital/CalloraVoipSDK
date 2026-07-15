using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the public video tap (public Video API, step a — sub-slice 3): receivers/senders created via
/// <see cref="MediaManager"/> attach to an <see cref="ICall"/> and move encoded frames both ways,
/// with the same fan-out, detach/dispose and state-guard semantics as the audio tap. Transport-only —
/// the payload is opaque encoded bytes.
/// </summary>
public sealed class PublicVideoTapContractTests : IDisposable
{
    private readonly MediaManager _media = new();
    private readonly SipCoreCallChannel _channel;
    private readonly Call _call;

    public PublicVideoTapContractTests()
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

    private static CallVideoFrame Inbound(byte marker, bool keyFrame = false) =>
        new([marker, 2, 3, 4], PayloadType: 96, RtpTimestamp: 9000, IsKeyFrame: keyFrame);

    [Fact]
    public void Two_attached_receivers_receive_the_same_inbound_frame()
    {
        using IVideoReceiver first = _media.CreateVideoReceiver();
        using IVideoReceiver second = _media.CreateVideoReceiver();
        var received = new List<(string Receiver, VideoFrame Frame)>();
        first.FrameReceived += (_, e) => received.Add(("first", e.Frame));
        second.FrameReceived += (_, e) => received.Add(("second", e.Frame));

        first.AttachToCall(_call);
        second.AttachToCall(_call);
        _channel.DeliverInboundVideoFrame(Inbound(0xAB, keyFrame: true));

        Assert.Equal(2, received.Count);
        Assert.Contains(received, r => r.Receiver == "first");
        Assert.Contains(received, r => r.Receiver == "second");
        Assert.All(received, r => Assert.Equal(0xAB, r.Frame.Payload.Span[0]));
        Assert.All(received, r => Assert.Equal(96, r.Frame.PayloadType));
        Assert.All(received, r => Assert.Equal(9000u, r.Frame.RtpTimestamp));
        Assert.All(received, r => Assert.True(r.Frame.IsKeyFrame));
    }

    [Fact]
    public void Detached_receiver_stops_receiving_without_affecting_others()
    {
        using IVideoReceiver first = _media.CreateVideoReceiver();
        using IVideoReceiver second = _media.CreateVideoReceiver();
        var firstCount = 0;
        var secondCount = 0;
        first.FrameReceived += (_, _) => firstCount++;
        second.FrameReceived += (_, _) => secondCount++;
        first.AttachToCall(_call);
        second.AttachToCall(_call);

        first.Detach();
        _channel.DeliverInboundVideoFrame(Inbound(0x01));

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public void Disposed_receiver_releases_its_listener()
    {
        IVideoReceiver first = _media.CreateVideoReceiver();
        using IVideoReceiver second = _media.CreateVideoReceiver();
        var firstCount = 0;
        var secondCount = 0;
        first.FrameReceived += (_, _) => firstCount++;
        second.FrameReceived += (_, _) => secondCount++;
        first.AttachToCall(_call);
        second.AttachToCall(_call);

        first.Dispose();
        _channel.DeliverInboundVideoFrame(Inbound(0x02));

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public async Task Attached_sender_delivers_frame_to_call_send_path()
    {
        var sent = new List<CallVideoFrame>();
        _channel.SetVideoSendDelegate((frame, _) =>
        {
            lock (sent) sent.Add(frame);
            return Task.CompletedTask;
        });
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);

        using IVideoSender sender = _media.CreateVideoSender();
        sender.AttachToCall(_call);
        await sender.SendAsync(new VideoFrame(new byte[] { 9, 9, 9 }, PayloadType: 96, RtpTimestamp: 12345, IsKeyFrame: true));

        var frame = Assert.Single(sent);
        Assert.Equal(96, frame.PayloadType);
        Assert.Equal(12345u, frame.RtpTimestamp);
        Assert.True(frame.IsKeyFrame);
        Assert.Equal(new byte[] { 9, 9, 9 }, frame.Payload);
    }

    [Fact]
    public async Task Sender_drops_frames_while_the_call_is_not_connected()
    {
        var sent = new List<CallVideoFrame>();
        _channel.SetVideoSendDelegate((frame, _) =>
        {
            lock (sent) sent.Add(frame);
            return Task.CompletedTask;
        });

        using IVideoSender sender = _media.CreateVideoSender();
        sender.AttachToCall(_call); // call is still in its initial (non-connected) state

        await sender.SendAsync(new VideoFrame(new byte[] { 1 }, 96, 1, IsKeyFrame: false));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task Sender_without_attachment_throws()
    {
        using IVideoSender sender = _media.CreateVideoSender();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(new VideoFrame(new byte[] { 1 }, 96, 1, IsKeyFrame: false)));
    }

    [Fact]
    public void Sender_surfaces_the_calls_congestion_recommendation()
    {
        using IVideoSender sender = _media.CreateVideoSender();
        sender.AttachToCall(_call);
        var events = new List<VideoBitrateRecommendationEventArgs>();
        sender.RecommendedBitrateChanged += (_, e) => events.Add(e);

        _call.SetVideoCongestion(500_000, NetworkQuality.Fair);

        Assert.Equal(500_000, sender.RecommendedBitrateBps);
        Assert.Equal(NetworkQuality.Fair, sender.NetworkQuality);
        var e = Assert.Single(events);
        Assert.Equal(500_000, e.RecommendedBitrateBps);
        Assert.Equal(NetworkQuality.Fair, e.NetworkQuality);
    }

    [Fact]
    public void Detached_sender_stops_receiving_congestion_updates()
    {
        using IVideoSender sender = _media.CreateVideoSender();
        sender.AttachToCall(_call);
        var count = 0;
        sender.RecommendedBitrateChanged += (_, _) => count++;

        sender.Detach();
        _call.SetVideoCongestion(400_000, NetworkQuality.Poor);

        Assert.Equal(0, count);
        Assert.Null(sender.RecommendedBitrateBps); // detached → no attached call
        Assert.Null(sender.NetworkQuality);
    }

    [Fact]
    public void Unattached_sender_reports_no_recommendation()
    {
        using IVideoSender sender = _media.CreateVideoSender();

        Assert.Null(sender.RecommendedBitrateBps);
        Assert.Null(sender.NetworkQuality);
    }

    [Fact]
    public void Disposed_sender_unsubscribes_from_congestion_updates()
    {
        IVideoSender sender = _media.CreateVideoSender();
        sender.AttachToCall(_call);
        var count = 0;
        sender.RecommendedBitrateChanged += (_, _) => count++;

        sender.Dispose();
        _call.SetVideoCongestion(300_000, NetworkQuality.Poor);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Sender_surfaces_the_calls_keyframe_request()
    {
        using IVideoSender sender = _media.CreateVideoSender();
        sender.AttachToCall(_call);
        var count = 0;
        sender.KeyFrameRequested += (_, _) => count++;

        _call.RaiseVideoKeyFrameRequested();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Detached_sender_stops_receiving_keyframe_requests()
    {
        using IVideoSender sender = _media.CreateVideoSender();
        sender.AttachToCall(_call);
        var count = 0;
        sender.KeyFrameRequested += (_, _) => count++;

        sender.Detach();
        _call.RaiseVideoKeyFrameRequested();

        Assert.Equal(0, count);
    }

    [Fact]
    public void Disposed_sender_unsubscribes_from_keyframe_requests()
    {
        IVideoSender sender = _media.CreateVideoSender();
        sender.AttachToCall(_call);
        var count = 0;
        sender.KeyFrameRequested += (_, _) => count++;

        sender.Dispose();
        _call.RaiseVideoKeyFrameRequested();

        Assert.Equal(0, count);
    }
}
