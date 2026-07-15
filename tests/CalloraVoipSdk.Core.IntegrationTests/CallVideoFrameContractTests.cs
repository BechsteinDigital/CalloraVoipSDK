using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the encoded-video-frame contract on the call channel and the <see cref="Call"/> aggregate
/// (public Video API, step a — sub-slice 1): send-delegate routing, inbound listener fan-out,
/// detach/dispose semantics, and the Connected/OnHold state guard on the send path.
/// </summary>
public sealed class CallVideoFrameContractTests : IDisposable
{
    private readonly SipCoreCallChannel _channel;
    private readonly Call _call;

    public CallVideoFrameContractTests()
    {
        _channel = NewChannel();
        _call = new Call(
            CallId.New(),
            CallDirection.Inbound,
            "sip:remote@test.invalid",
            _channel,
            new FakePhoneLine(),
            NullLogger<Call>.Instance);
    }

    public void Dispose() => _channel.Dispose();

    private static SipCoreCallChannel NewChannel() =>
        new(NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

    private static CallVideoFrame TestFrame(byte marker, bool keyFrame = false) =>
        new([marker, 2, 3, 4], PayloadType: 96, RtpTimestamp: 9000, IsKeyFrame: keyFrame);

    [Fact]
    public void Two_listeners_receive_the_same_inbound_frame()
    {
        var received = new List<(string Who, CallVideoFrame Frame)>();
        void First(CallVideoFrame f) => received.Add(("first", f));
        void Second(CallVideoFrame f) => received.Add(("second", f));
        _channel.AddVideoFrameListener(First);
        _channel.AddVideoFrameListener(Second);

        _channel.DeliverInboundVideoFrame(TestFrame(0xAB, keyFrame: true));

        Assert.Equal(2, received.Count);
        Assert.Contains(received, r => r.Who == "first");
        Assert.Contains(received, r => r.Who == "second");
        Assert.All(received, r => Assert.Equal(0xAB, r.Frame.Payload[0]));
        Assert.All(received, r => Assert.True(r.Frame.IsKeyFrame));
    }

    [Fact]
    public void Removed_listener_stops_receiving_without_affecting_others()
    {
        var firstCount = 0;
        var secondCount = 0;
        void First(CallVideoFrame _) => firstCount++;
        void Second(CallVideoFrame _) => secondCount++;
        _channel.AddVideoFrameListener(First);
        _channel.AddVideoFrameListener(Second);

        _channel.RemoveVideoFrameListener(First);
        _channel.DeliverInboundVideoFrame(TestFrame(0x01));

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public void A_faulting_listener_does_not_prevent_the_others()
    {
        var reached = false;
        _channel.AddVideoFrameListener(_ => throw new InvalidOperationException("boom"));
        _channel.AddVideoFrameListener(_ => reached = true);

        _channel.DeliverInboundVideoFrame(TestFrame(0x02));

        Assert.True(reached);
    }

    [Fact]
    public void Adding_a_listener_after_dispose_throws()
    {
        var channel = NewChannel();
        channel.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => channel.AddVideoFrameListener(_ => { }));
    }

    [Fact]
    public async Task Send_routes_to_the_configured_delegate()
    {
        var sent = new List<CallVideoFrame>();
        _channel.SetVideoSendDelegate((frame, _) =>
        {
            lock (sent) sent.Add(frame);
            return Task.CompletedTask;
        });

        await _channel.SendVideoFrameAsync(TestFrame(0x09, keyFrame: true));

        var frame = Assert.Single(sent);
        Assert.Equal(96, frame.PayloadType);
        Assert.Equal(9000u, frame.RtpTimestamp);
        Assert.True(frame.IsKeyFrame);
    }

    [Fact]
    public async Task Send_without_a_delegate_is_a_no_op()
    {
        // No delegate set: must complete quietly rather than throw.
        await _channel.SendVideoFrameAsync(TestFrame(0x05));
    }

    [Fact]
    public async Task Call_send_is_rejected_before_the_call_is_connected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _call.SendVideoFrameAsync(TestFrame(0x01)));
    }

    [Fact]
    public async Task Call_send_reaches_the_channel_when_connected()
    {
        var sent = new List<CallVideoFrame>();
        _channel.SetVideoSendDelegate((frame, _) =>
        {
            lock (sent) sent.Add(frame);
            return Task.CompletedTask;
        });
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);

        await _call.SendVideoFrameAsync(TestFrame(0x07));

        Assert.Single(sent);
    }

    [Fact]
    public void Call_listener_registration_delegates_to_the_channel()
    {
        var count = 0;
        void Listener(CallVideoFrame _) => count++;
        _call.AddVideoFrameListener(Listener);

        _channel.DeliverInboundVideoFrame(TestFrame(0x03));
        Assert.Equal(1, count);

        _call.RemoveVideoFrameListener(Listener);
        _channel.DeliverInboundVideoFrame(TestFrame(0x04));
        Assert.Equal(1, count);
    }
}
