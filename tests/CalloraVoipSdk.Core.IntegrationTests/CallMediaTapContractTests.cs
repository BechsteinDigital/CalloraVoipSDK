using System.Net;
using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the public per-call media tap contract (CORE-301 part 1):
/// receiver fan-out, detach/dispose semantics, sender injection and format discovery.
/// </summary>
public sealed class CallMediaTapContractTests : IDisposable
{
    private readonly SipCoreCallChannel _channel;
    private readonly Call _call;

    public CallMediaTapContractTests()
    {
        _channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

        _call = new Call(
            CallId.New(),
            CallDirection.Inbound,
            "sip:remote@test.invalid",
            _channel,
            new FakePhoneLine(),
            NullLogger<Call>.Instance);
    }

    public void Dispose() => _channel.Dispose();

    private static CallAudioFrame TestFrame(byte marker) =>
        new([marker, 2, 3, 4], PayloadType: 0, DurationRtpUnits: 160);

    [Fact]
    public void Two_attached_receivers_receive_the_same_inbound_frame()
    {
        using IMediaReceiver first = new MediaReceiver();
        using IMediaReceiver second = new MediaReceiver();
        var received = new List<(string Receiver, MediaFrame Frame)>();
        first.FrameReceived += (_, e) => received.Add(("first", e.Frame));
        second.FrameReceived += (_, e) => received.Add(("second", e.Frame));

        first.AttachToCall(_call);
        second.AttachToCall(_call);
        _channel.DeliverInboundAudioFrame(TestFrame(0xAB));

        Assert.Equal(2, received.Count);
        Assert.Contains(received, r => r.Receiver == "first");
        Assert.Contains(received, r => r.Receiver == "second");
        Assert.All(received, r => Assert.Equal(0xAB, r.Frame.Payload.Span[0]));
    }

    [Fact]
    public void Detached_receiver_stops_receiving_without_affecting_others()
    {
        using IMediaReceiver first = new MediaReceiver();
        using IMediaReceiver second = new MediaReceiver();
        var firstCount = 0;
        var secondCount = 0;
        first.FrameReceived += (_, _) => firstCount++;
        second.FrameReceived += (_, _) => secondCount++;
        first.AttachToCall(_call);
        second.AttachToCall(_call);

        first.Detach();
        _channel.DeliverInboundAudioFrame(TestFrame(0x01));

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public void Disposed_receiver_releases_its_listener()
    {
        IMediaReceiver first = new MediaReceiver();
        using IMediaReceiver second = new MediaReceiver();
        var firstCount = 0;
        var secondCount = 0;
        first.FrameReceived += (_, _) => firstCount++;
        second.FrameReceived += (_, _) => secondCount++;
        first.AttachToCall(_call);
        second.AttachToCall(_call);

        first.Dispose();
        _channel.DeliverInboundAudioFrame(TestFrame(0x02));

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public async Task Attached_sender_delivers_frame_to_call_send_path()
    {
        var sent = new List<CallAudioFrame>();
        _channel.SetAudioSendDelegate((frame, _) =>
        {
            lock (sent) sent.Add(frame);
            return Task.CompletedTask;
        });
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);

        using IMediaSender sender = new MediaSender();
        sender.AttachToCall(_call);
        await sender.SendAsync(new MediaFrame(new byte[] { 9, 9, 9 }, PayloadType: 8, DurationRtpUnits: 160));

        var frame = Assert.Single(sent);
        Assert.Equal(8, frame.PayloadType);
        Assert.Equal(new byte[] { 9, 9, 9 }, frame.Payload);
    }

    [Fact]
    public void Negotiated_media_parameters_are_readable_through_public_call_contract()
    {
        _call.SetMediaParameters(new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 40000),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 40002),
            PayloadType = 9,
            ClockRate = 8000,
            SamplesPerPacket = 160,
        });

        ICall publicView = _call;

        Assert.NotNull(publicView.MediaParameters);
        Assert.Equal(9, publicView.MediaParameters!.PayloadType);
        Assert.Equal(8000, publicView.MediaParameters.ClockRate);
    }
}

/// <summary>Minimal line stub; the media tap contract never touches line behavior.</summary>
internal sealed class FakePhoneLine : IPhoneLine
{
    public LineId LineId { get; } = LineId.New();

    public SipAccount Account { get; } = new()
    {
        SipServer = "test.invalid",
        Username = "tester",
        Password = "secret",
        DisplayName = "tester",
    };

    public LineState State => LineState.Registered;

#pragma warning disable CS0067
    public event EventHandler<LineStateChangedEventArgs>? StateChanged;
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;
    public event EventHandler<OutboundCallRingingEventArgs>? OutboundCallRinging;
    public event EventHandler<LineReconnectingEventArgs>? LineReconnecting;
    public event EventHandler<LineReconnectFailedEventArgs>? LineReconnectFailed;
#pragma warning restore CS0067

    public Task<ICall> DialAsync(string targetUri, DialOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task UnregisterAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();
}
