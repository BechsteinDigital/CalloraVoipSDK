using CalloraVoipSdk.Core.Application.Observability;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P3-14: one event contract across the SDK's public facades. Three disciplines used to coexist —
/// the SIP facade forwarded its events with a bare Invoke (a throwing app handler reached the signalling path
/// and blocked the later subscribers), the peer facade snapshotted under its lock but invoked the whole
/// multicast delegate in one call (isolating nothing), and only the telemetry path from P1-3 isolated per
/// subscriber. That last one is now the rule everywhere: a subscriber fault is logged, never propagated into
/// the SDK path, and never keeps the remaining subscribers from running.
/// </summary>
public sealed class SdkEventContractTests
{
    [Fact]
    public void A_throwing_subscriber_neither_propagates_nor_blocks_the_later_ones()
    {
        var fired = new List<string>();
        EventHandler<int>? handlers = null;
        handlers += (_, _) => fired.Add("first");
        handlers += (_, _) => throw new InvalidOperationException("subscriber-boom");
        handlers += (_, _) => fired.Add("third");

        var exception = Record.Exception(
            () => SdkEventDispatch.Raise(handlers, this, 42, NullLogger.Instance, "TestEvent"));

        Assert.Null(exception);
        Assert.Equal(["first", "third"], fired);
    }

    [Fact]
    public void The_payload_free_and_action_shaped_fan_outs_follow_the_same_contract()
    {
        var plain = 0;
        EventHandler? plainHandlers = null;
        plainHandlers += (_, _) => throw new InvalidOperationException("subscriber-boom");
        plainHandlers += (_, _) => plain++;

        var records = new List<string>();
        Action<string>? actionHandlers = null;
        actionHandlers += _ => throw new InvalidOperationException("subscriber-boom");
        actionHandlers += records.Add;

        Assert.Null(Record.Exception(
            () => SdkEventDispatch.Raise(plainHandlers, this, NullLogger.Instance, "PlainEvent")));
        Assert.Null(Record.Exception(
            () => SdkEventDispatch.Raise(actionHandlers, "record", NullLogger.Instance, "ActionEvent")));

        Assert.Equal(1, plain);
        Assert.Equal(["record"], records);
    }

    /// <summary>
    /// The per-frame variant trades multi-subscriber isolation for an allocation-free raise (K3), but still must
    /// not let a throwing handler reach the media receive loop.
    /// </summary>
    [Fact]
    public void The_media_path_variant_still_keeps_a_throwing_subscriber_off_the_receive_loop()
    {
        EventHandler<int>? handlers = null;
        handlers += (_, _) => throw new InvalidOperationException("frame-handler-boom");

        Assert.Null(Record.Exception(
            () => SdkEventDispatch.RaiseOnMediaPath(handlers, this, 1, NullLogger.Instance, "FrameReceived")));
    }

    [Fact]
    public void A_null_delegate_is_a_no_op_on_every_shape()
    {
        Assert.Null(Record.Exception(() =>
        {
            SdkEventDispatch.Raise<int>(null, this, 1, NullLogger.Instance, "E");
            SdkEventDispatch.Raise(null, this, NullLogger.Instance, "E");
            SdkEventDispatch.Raise<string>(null, "r", NullLogger.Instance, "E");
            SdkEventDispatch.RaiseOnMediaPath<int>(null, this, 1, NullLogger.Instance, "E");
        }));
    }

    [Fact]
    public async Task The_peer_facade_isolates_each_state_subscriber()
    {
        await using var client = new WebRtcClient();
        var peer = client.CreatePeer();

        var fired = new List<string>();
        peer.ConnectionStateChanged += (_, _) => fired.Add("first");
        peer.ConnectionStateChanged += (_, _) => throw new InvalidOperationException("subscriber-boom");
        peer.ConnectionStateChanged += (_, _) => fired.Add("third");

        await peer.DisposeAsync();

        // The terminal Closed transition (#166 P2-6) reached both good subscribers despite the throwing one.
        Assert.Equal(["first", "third"], fired);
    }

    [Fact]
    public void The_telemetry_manager_isolates_each_subscriber()
    {
        var sink = new ClientTelemetrySink(new CollectingInnerSink(), NullLogger.Instance);
        var telemetry = new TelemetryManager(sink, NullLogger.Instance);

        var fired = 0;
        telemetry.EventPublished += (_, _) => throw new InvalidOperationException("subscriber-boom");
        telemetry.EventPublished += (_, _) => fired++;

        var exception = Record.Exception(() => sink.PublishEvent(new SipEventRecord { EventType = "test" }));

        // Before the fix the manager was ONE subscriber of the sink, so its own fan-out was a single Invoke:
        // the sink isolated the manager, but the first throwing app handler still blocked the second.
        Assert.Null(exception);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_throwing_frame_subscriber_does_not_break_the_inbound_media_projection()
    {
        var set = new RemoteTrackSet(track => track.FrameReceived += (_, _) => throw new InvalidOperationException("frame-boom"));

        var exception = Record.Exception(() => set.DeliverAudioFrame(
            mid: null, streamId: null, trackId: null,
            new EncodedFrame(new byte[] { 1 }, rtpTimestamp: 0, isKeyFrame: false, presentationTimeUsec: null)));

        Assert.Null(exception);
    }

    private sealed class CollectingInnerSink : ISipTelemetrySink
    {
        public void PublishEvent(SipEventRecord record)
        {
        }

        public void PublishMetric(SipMetricRecord record)
        {
        }

        public void PublishCdr(SipCdrRecord record)
        {
        }
    }
}
