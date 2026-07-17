using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end regression net for the <see cref="TurnServer"/> UDP allocation and relay lifecycle
/// (closes the audit gap "no E2E TURN harness", A6/B1). A real server runs on a loopback socket and a
/// <see cref="RawTurnUdpClient"/> drives the full flow over one stable 5-tuple:
/// Allocate → CreatePermission → Send/ChannelData relay in both directions → Refresh teardown, plus the
/// permission-gating drops. This exercises the intertwined request-dispatch, allocation-registry and
/// relay-loop core, so it protects the planned decomposition of that God-class. The server runs with
/// <see cref="TurnServerOptions.RequireAuthentication"/> off; the authenticated challenge/nonce path is
/// covered separately and left as a follow-up.
/// </summary>
public sealed class TurnServerE2eTests
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DropWindow = TimeSpan.FromMilliseconds(500);

    private static TurnServer CreateStartedServer(IStunMessageCodec codec)
    {
        var server = new TurnServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            TurnServerTransport.Udp,
            codec,
            NullLogger<TurnServer>.Instance,
            authOptions: null,
            tlsServerCertificate: null,
            options: new TurnServerOptions { RequireAuthentication = false });
        server.Start();
        return server;
    }

    [Fact]
    public async Task Allocate_returns_a_loopback_relayed_endpoint()
    {
        var codec = new StunMessageCodec();
        await using var server = CreateStartedServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);

        var allocation = await client.AllocateAsync();

        Assert.NotNull(allocation.RelayedEndPoint);
        Assert.Equal(IPAddress.Loopback, allocation.RelayedEndPoint.Address);
        Assert.NotEqual(0, allocation.RelayedEndPoint.Port);
        // Default server lifetime is granted when the client does not request one.
        Assert.True(allocation.LifetimeSeconds > 0, "Allocate must grant a positive lifetime.");
    }

    [Fact]
    public async Task Send_indication_reaches_a_permitted_peer()
    {
        var codec = new StunMessageCodec();
        await using var server = CreateStartedServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var allocation = await client.AllocateAsync();
        await client.CreatePermissionAsync(peerEndPoint);

        var payload = "send-indication-out"u8.ToArray();
        await client.SendIndicationAsync(peerEndPoint, payload);

        var received = await ReceiveWithTimeoutAsync(peer, RelayTimeout);
        Assert.Equal(payload, received.Buffer);
        // The relayed datagram must arrive from the allocation's relayed address.
        Assert.Equal(allocation.RelayedEndPoint.Port, received.RemoteEndPoint.Port);
    }

    [Fact]
    public async Task Peer_datagram_returns_as_a_data_indication()
    {
        var codec = new StunMessageCodec();
        await using var server = CreateStartedServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var allocation = await client.AllocateAsync();
        await client.CreatePermissionAsync(peerEndPoint);

        var payload = "peer-to-client-in"u8.ToArray();
        await peer.SendAsync(payload, allocation.RelayedEndPoint);

        var inbound = await client.ReceiveRelayAsync();
        Assert.False(inbound.IsChannelData, "Permission-only relay must return a Data indication, not ChannelData.");
        Assert.Equal(payload, inbound.Data);
        Assert.NotNull(inbound.PeerEndPoint);
        Assert.Equal(peerEndPoint.Port, inbound.PeerEndPoint!.Port);
    }

    [Fact]
    public async Task Channel_data_relays_in_both_directions()
    {
        const ushort channel = 0x4001;
        var codec = new StunMessageCodec();
        await using var server = CreateStartedServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var allocation = await client.AllocateAsync();
        await client.CreatePermissionAsync(peerEndPoint);
        await client.ChannelBindAsync(channel, peerEndPoint);

        // Client → peer over ChannelData.
        var outbound = "channel-out"u8.ToArray();
        await client.SendChannelDataAsync(channel, outbound);
        var atPeer = await ReceiveWithTimeoutAsync(peer, RelayTimeout);
        Assert.Equal(outbound, atPeer.Buffer);

        // Peer → client: a bound channel makes the server return ChannelData, not a Data indication.
        var inboundPayload = "channel-in"u8.ToArray();
        await peer.SendAsync(inboundPayload, allocation.RelayedEndPoint);
        var inbound = await client.ReceiveRelayAsync();
        Assert.True(inbound.IsChannelData, "A bound channel must relay inbound peer data as ChannelData.");
        Assert.Equal(channel, inbound.ChannelNumber);
        Assert.Equal(inboundPayload, inbound.Data);
    }

    [Fact]
    public async Task Send_without_permission_is_dropped()
    {
        var codec = new StunMessageCodec();
        await using var server = CreateStartedServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        await client.AllocateAsync();
        // Deliberately no CreatePermission: the server must not relay to an unpermitted peer.
        await client.SendIndicationAsync(peerEndPoint, "no-permission"u8.ToArray());

        Assert.False(await ReceivedWithinAsync(peer, DropWindow),
            "A Send indication without a matching permission must be dropped.");
    }

    [Fact]
    public async Task Refresh_with_zero_lifetime_deletes_the_allocation()
    {
        var codec = new StunMessageCodec();
        await using var server = CreateStartedServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        await client.AllocateAsync();
        await client.CreatePermissionAsync(peerEndPoint);

        var grantedLifetime = await client.RefreshAsync(0);
        Assert.Equal(0u, grantedLifetime);

        // The allocation is gone, so a subsequent Send has no relay to traverse.
        await client.SendIndicationAsync(peerEndPoint, "after-teardown"u8.ToArray());
        Assert.False(await ReceivedWithinAsync(peer, DropWindow),
            "After a zero-lifetime Refresh the allocation must be removed and relay must stop.");
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
