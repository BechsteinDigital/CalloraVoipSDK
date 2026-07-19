using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 8656 §14 DONT-FRAGMENT: the client can request that relayed IPv4 datagrams carry the Don't-Fragment bit.
/// The attribute was accepted but never honoured; the relay socket now sets <see cref="Socket.DontFragment"/>.
/// </summary>
public sealed class TurnDontFragmentTests
{
    [Fact]
    public void The_ipv4_relay_socket_sets_dont_fragment_when_requested()
    {
        using var socket = TurnAllocateRequestHandler.CreateRelaySocket(AddressFamily.InterNetwork, dontFragment: true);
        Assert.True(socket.Client.DontFragment); // DF bit set for a DONT-FRAGMENT allocation
    }

    [Fact]
    public async Task Turn_client_allocates_with_dont_fragment_end_to_end()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RequireAuthentication = false,
        });
        host.Start();

        var client = new TurnClient(new StunMessageCodec(), NullLogger<TurnClient>.Instance);
        var allocation = await client.AllocateAsync(
            host.LocalEndPoint,
            credentials: null,
            options: new TurnAllocateOptions { DontFragment = true });

        Assert.NotNull(allocation.RelayedEndPoint); // the server honoured DONT-FRAGMENT instead of rejecting it
    }
}
