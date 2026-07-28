using CalloraVoipSdk.InteropHarness.Chaos;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Chaos;

/// <summary>
/// CORE-011 chaos gate — Fault class 1 (transport loss mid-call). A running media session survives a total
/// loss of the media path (the relay drops everything) without a crash or an escaping exception, and recovers
/// when the path heals. This is the graceful-degradation guarantee for a network drop mid-call.
/// </summary>
public sealed class MediaTransportLossChaosTests
{
    private static readonly byte[] Payload = new byte[160];

    [Fact, Trait("Category", "Chaos")]
    public async Task Media_survives_and_recovers_from_a_mid_call_transport_loss()
    {
        await using var loop = await ChaosRtpMediaLoopback.StartAsync();

        // 1. Healthy: media flows A → B through the relay.
        Assert.True(
            await loop.TryRoundTripAsync(Payload, TimeSpan.FromSeconds(3)),
            "media should flow before the fault is injected");

        // 2. Mid-call transport loss: the relay drops everything. The sender keeps sending (its socket is up),
        //    so the unambiguous evidence that the transport is down is the relay forwarding NOTHING new while
        //    the drop counter climbs — the receiver's playout may still drain its jitter buffer for a moment,
        //    so that is deliberately not the signal. The key GA guarantee: the sender tolerates the loss and
        //    SendForAsync completes without an escaping exception.
        loop.Relay.HardFault();
        await Task.Delay(100); // let any in-flight forward from before the fault settle
        var forwardedAtFault = loop.Relay.Forwarded;
        var droppedAtFault = loop.Relay.Dropped;

        await loop.SendForAsync(Payload, TimeSpan.FromSeconds(1.5));

        Assert.Equal(forwardedAtFault, loop.Relay.Forwarded); // nothing got through during the sustained loss
        Assert.True(loop.Relay.Dropped > droppedAtFault, "the transport loss should keep dropping datagrams");

        // 3. Recovery: the path heals and media flows again without re-establishing the session.
        loop.Relay.Heal();
        Assert.True(
            await loop.TryRoundTripAsync(Payload, TimeSpan.FromSeconds(5)),
            "media should recover once the transport loss clears");
        Assert.True(loop.Relay.Forwarded > forwardedAtFault, "media should be forwarded again after recovery");
    }
}
