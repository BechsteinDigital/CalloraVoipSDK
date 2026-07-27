using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Hardening gates for the WebRTC client facade (issue #18): thread-safe peer events (#18.3), a monotone
/// rate clock (#18.4), client-level teardown that closes tracked peers (#18.7), and startup validation of
/// <see cref="WebRtcOptions"/> (#18.2).
/// </summary>
public sealed class WebRtcClientHardeningTests
{
    // 18.3 — concurrent subscribe/unsubscribe on a peer event must not lose handlers. The buggy field-like
    // accessors used a non-atomic += / -=, so racing subscribers could clobber each other's writes. Bounded
    // parallelism (Parallel.For) creates real contention without starving the thread pool.
    [Fact]
    public async Task PeerConnection_events_do_not_lose_handlers_under_concurrent_subscribe()
    {
        await using var rtc = new WebRtcClient();
        await using var peer = rtc.CreatePeer();

        const int handlerCount = 500;
        var handlers = new EventHandler<PeerConnectionState>[handlerCount];
        for (var i = 0; i < handlerCount; i++)
        {
            handlers[i] = (_, _) => { };
        }

        // Add all handlers with maximum contention. With a non-atomic accessor a subset of the adds is lost
        // (read-modify-write races clobber each other); under the lock every add survives.
        await Task.Run(() => Parallel.ForEach(handlers, h => peer.ConnectionStateChanged += h));
        Assert.Equal(handlerCount, SubscriberCount(peer));

        // And symmetric removal drains back to zero under the same contention.
        await Task.Run(() => Parallel.ForEach(handlers, h => peer.ConnectionStateChanged -= h));
        Assert.Equal(0, SubscriberCount(peer));
    }

    private static int SubscriberCount(IPeerConnection peer)
    {
        var field = peer.GetType().GetField(
            "_connectionStateChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var handler = (Delegate?)field.GetValue(peer);
        return handler?.GetInvocationList().Length ?? 0;
    }

    // 18.4 — the rate clock must be the uptime-based monotone counter, not the wall clock. DateTime.UtcNow.Ticks
    // is a year-epoch value (~6.3e17); the monotone counter is process uptime in 100 ns ticks (far smaller and
    // never affected by an NTP step). Pinning the magnitude proves the source was switched off the wall clock.
    [Fact]
    public void Rate_clock_is_monotone_uptime_not_wall_clock()
    {
        var first = PeerConnection.MonotonicTicks();
        var second = PeerConnection.MonotonicTicks();

        Assert.True(second >= first, "monotone clock must not go backwards");
        Assert.True(
            first < DateTime.UtcNow.Ticks / 2,
            "rate clock must be the uptime counter, not DateTime.UtcNow.Ticks");
    }

    // 18.7 — disposing the client closes every peer it still tracks and empties the registry.
    [Fact]
    public async Task Disposing_the_client_disposes_tracked_peers()
    {
        var rtc = new WebRtcClient();
        var first = rtc.CreatePeer();
        var second = rtc.CreatePeer();

        Assert.Equal(2, rtc.Peers.Count);

        await rtc.DisposeAsync();

        Assert.Empty(rtc.Peers.Active);
        Assert.Equal(PeerConnectionState.Closed, first.State);
        Assert.Equal(PeerConnectionState.Closed, second.State);
    }

    [Fact]
    public async Task Client_dispose_is_idempotent()
    {
        var rtc = new WebRtcClient();
        _ = rtc.CreatePeer();

        await rtc.DisposeAsync();
        var second = await Record.ExceptionAsync(async () => await rtc.DisposeAsync());

        Assert.Null(second);
    }

    // 18.2 — an invalid ICE server surfaces at start (when the validated options are first resolved), not
    // only later at CreatePeer. Mirrors AddCalloraVoip's ValidateOnStart symmetry.
    [Fact]
    public void Invalid_ice_server_options_fail_validation()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc(options =>
            options.IceServers =
            [
                new IceServerConfiguration { Type = IceServerType.Turn, Host = "turn.example.com" }, // no creds
            ]);

        using var provider = services.BuildServiceProvider();

        // Resolving the validated options triggers ValidateOnStart's IValidateOptions run.
        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<WebRtcOptions>>().Value);

        Assert.Contains("Username is required", string.Join("; ", ex.Failures), StringComparison.Ordinal);
        Assert.Contains("Password is required", string.Join("; ", ex.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_ice_server_options_pass_validation()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc()
            .WithStunServer("stun.example.com")
            .WithTurnServer("turn.example.com", "user", "secret", port: 3478);

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<WebRtcOptions>>().Value;
        Assert.Equal(2, options.IceServers.Count);
    }
}
