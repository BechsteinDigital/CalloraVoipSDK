using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Regression for the ICE gathering bind conflict found in the first real STUN test:
/// the RTP media port is reserved by the call while gathering runs, so the STUN query
/// must go through that shared socket — binding a second socket to the port fails with
/// "address already in use" (SocketException 98/10048).
/// </summary>
public sealed class StunSharedSocketTests
{
    [Fact]
    public async Task Binding_query_over_the_reserved_media_socket_succeeds()
    {
        var codec = new StunMessageCodec();
        await using var server = new StunServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            codec,
            responseIntegrityKey: null,
            NullLogger<StunServer>.Instance);
        server.Start(new StunBindingRequestHandler(codec, NullLogger<StunBindingRequestHandler>.Instance));

        // The call's port-reservation socket: bound, unconnected, owned by the channel.
        using var mediaSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        mediaSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var mediaEndPoint = (IPEndPoint)mediaSocket.LocalEndPoint!;

        var client = new StunClient(codec, NullLogger<StunClient>.Instance);
        var result = await client.QueryBindingAsync(
            server.LocalEndPoint,
            sharedUdpSocket: mediaSocket);

        // Loopback: the mapped endpoint the server sees IS the media socket's endpoint.
        Assert.Equal(mediaEndPoint, result.MappedEndPoint);

        // The client must not have disposed or connected the caller-owned socket.
        Assert.True(mediaSocket.IsBound);
        Assert.Null(mediaSocket.Connected ? mediaSocket.RemoteEndPoint : null);
    }

    [Fact]
    public async Task Binding_a_second_socket_to_the_reserved_port_fails_without_sharing()
    {
        var codec = new StunMessageCodec();
        await using var server = new StunServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            codec,
            responseIntegrityKey: null,
            NullLogger<StunServer>.Instance);
        server.Start(new StunBindingRequestHandler(codec, NullLogger<StunBindingRequestHandler>.Instance));

        using var mediaSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        mediaSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var mediaEndPoint = (IPEndPoint)mediaSocket.LocalEndPoint!;

        var client = new StunClient(codec, NullLogger<StunClient>.Instance);

        // Documents the failure mode this feature exists for: same port, no sharing.
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
            _ = await client.QueryBindingAsync(
                server.LocalEndPoint,
                localEndPoint: mediaEndPoint));
    }
}
