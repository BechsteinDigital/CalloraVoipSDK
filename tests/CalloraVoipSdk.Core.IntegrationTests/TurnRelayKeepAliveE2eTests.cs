using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end proof (CF-003) that the relay keepalive loops keep a REAL <see cref="TurnServer"/>'s allocation,
/// permission, and channel binding alive past their (accelerated) lifetime, so relayed media still flows after
/// the point an un-refreshed allocation would have been swept. The production loops
/// (<see cref="TurnAllocationRefreshLoop"/> / <see cref="TurnPermissionRefreshLoop"/> /
/// <see cref="TurnChannelRebindLoop"/>) run with their real <c>Task.Delay</c> cadence (lifetime/2) against a real
/// server over loopback, with short server lifetimes. This closes the "only fake-server coverage" gap: the earlier
/// loop tests injected the clock and answered from a fake TURN server; here real wall-clock refresh/rebind cycles
/// hit the real server. Real-time test (a handful of seconds).
/// </summary>
public sealed class TurnRelayKeepAliveE2eTests
{
    // Short server lifetimes so refresh cadence (lifetime/2 = 1 s) and expiry happen within a few seconds.
    private const uint LifetimeSeconds = 2;
    private static readonly TimeSpan PastOneLifetimeWithKeepalive = TimeSpan.FromSeconds(3);  // > lifetime, ~3 refreshes
    private static readonly TimeSpan WellPastExpiryNoKeepalive = TimeSpan.FromSeconds(5);     // >> lifetime + sweep
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DropWindow = TimeSpan.FromMilliseconds(800);

    private static TurnServer CreateShortLifetimeServer(StunMessageCodec codec)
    {
        var server = new TurnServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            TurnServerTransport.Udp,
            codec,
            NullLogger<TurnServer>.Instance,
            authOptions: null,
            tlsServerCertificate: null,
            options: new TurnServerOptions
            {
                RequireAuthentication = false,
                DefaultAllocationLifetimeSeconds = LifetimeSeconds,
                MaxAllocationLifetimeSeconds = LifetimeSeconds,
                PermissionLifetimeSeconds = LifetimeSeconds,
                ChannelBindingLifetimeSeconds = LifetimeSeconds,
                AllocationSweepIntervalSeconds = 1,
            });
        server.Start();
        return server;
    }

    [Fact]
    public async Task Without_keepalive_the_relay_drops_after_the_allocation_lifetime()
    {
        // Control: the server really does expire an un-refreshed allocation/permission/channel, so the survival in
        // the next test is meaningful rather than an artefact of a too-long lifetime.
        const ushort channel = 0x4002;
        var codec = new StunMessageCodec();
        await using var server = CreateShortLifetimeServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        await client.AllocateAsync();
        await client.CreatePermissionAsync(peerEndPoint);
        await client.ChannelBindAsync(channel, peerEndPoint);

        await Task.Delay(WellPastExpiryNoKeepalive); // no refresh → state lapses and the sweep removes it

        await client.SendChannelDataAsync(channel, "after-expiry"u8.ToArray());
        Assert.False(await ReceivedWithinAsync(peer, DropWindow),
            "Without keepalive the relay state must have expired; the datagram must not reach the peer.");
    }

    [Fact]
    public async Task Keepalive_loops_keep_the_relay_alive_past_the_lifetime_against_the_real_server()
    {
        const ushort channel = 0x4003;
        var codec = new StunMessageCodec();
        await using var server = CreateShortLifetimeServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var allocation = await client.AllocateAsync();
        await client.CreatePermissionAsync(peerEndPoint);
        await client.ChannelBindAsync(channel, peerEndPoint);

        // The single-socket raw client is not thread-safe, so a gate serialises the three loops' control
        // round-trips (production instead relies on the transactor's txn-id correlation on the shared socket).
        var gate = new SemaphoreSlim(1, 1);
        async Task<T> Gated<T>(Func<Task<T>> op)
        {
            await gate.WaitAsync();
            try { return await op(); }
            finally { gate.Release(); }
        }

        // Real loops, real cadence (lifetime/2 = 1 s), real server. No injected clock.
        await using var allocationLoop = new TurnAllocationRefreshLoop(
            async (_, lifetime, ct) =>
                new TurnRefreshResult { LifetimeSeconds = await Gated(() => client.RefreshAsync(lifetime, ct)) },
            initialCredentials: null, grantedLifetimeSeconds: LifetimeSeconds, NullLoggerFactory.Instance);

        await using var permissionLoop = new TurnPermissionRefreshLoop(
            async ct => await Gated(async () => { await client.CreatePermissionAsync(peerEndPoint, ct); return true; }),
            NullLoggerFactory.Instance, permissionLifetimeSeconds: LifetimeSeconds);

        await using var channelLoop = new TurnChannelRebindLoop(
            async (creds, ct) =>
            {
                await Gated(async () => { await client.ChannelBindAsync(channel, peerEndPoint, ct); return true; });
                return creds;
            },
            initialCredentials: null, NullLoggerFactory.Instance, channelLifetimeSeconds: LifetimeSeconds);

        allocationLoop.Start();
        permissionLoop.Start();
        channelLoop.Start();

        await Task.Delay(PastOneLifetimeWithKeepalive); // spans > 1 lifetime; each loop refreshed ~2-3 times

        // The refreshed permission + channel still carry a ChannelData datagram to the peer — well after an
        // un-refreshed allocation (control test) would already have been swept.
        await Gated(async () => { await client.SendChannelDataAsync(channel, "kept-alive"u8.ToArray()); return true; });
        var atPeer = await ReceiveWithTimeoutAsync(peer, RelayTimeout);

        Assert.Equal("kept-alive"u8.ToArray(), atPeer.Buffer);
        Assert.Equal(allocation.RelayedEndPoint.Port, atPeer.RemoteEndPoint.Port);
    }

    private static async Task<UdpReceiveResult> ReceiveWithTimeoutAsync(UdpClient socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await socket.ReceiveAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No datagram received within {timeout.TotalMilliseconds:F0} ms.");
        }
    }

    private static async Task<bool> ReceivedWithinAsync(UdpClient socket, TimeSpan window)
    {
        using var cts = new CancellationTokenSource(window);
        try
        {
            _ = await socket.ReceiveAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
