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
/// RFC 8656 §7 EVEN-PORT and RESERVATION-TOKEN: the client can request an even relayed port and reserve the
/// next (odd) one, then claim the reserved port with the returned token. The attributes were accepted but never
/// honoured; the server now binds an even port pair, reserves the companion, and issues/consumes the token.
/// </summary>
public sealed class TurnEvenPortReservationTests
{
    [Fact]
    public void Even_port_socket_pair_binds_an_even_port_and_the_next_odd_port()
    {
        var (even, reserved) = TurnAllocateRequestHandler.CreateEvenPortRelaySockets(
            AddressFamily.InterNetwork, reservePair: true, dontFragment: false);
        using (even)
        using (reserved)
        {
            var evenPort = ((IPEndPoint)even.Client.LocalEndPoint!).Port;
            Assert.Equal(0, evenPort % 2);                                                 // even relayed port
            Assert.NotNull(reserved);
            Assert.Equal(evenPort + 1, ((IPEndPoint)reserved!.Client.LocalEndPoint!).Port); // reserved companion port
        }
    }

    [Fact]
    public async Task Reserve_an_even_port_then_claim_the_companion_port_with_the_token()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RequireAuthentication = false,
        });
        host.Start();

        var client = new TurnClient(new StunMessageCodec(), NullLogger<TurnClient>.Instance);

        var reserved = await client.AllocateAsync(
            host.LocalEndPoint, credentials: null, options: new TurnAllocateOptions { ReserveEvenPort = true });
        Assert.Equal(0, reserved.RelayedEndPoint.Port % 2); // the relayed port is even
        Assert.NotNull(reserved.ReservationToken);          // and a token to claim its companion was returned

        var claimed = await client.AllocateAsync(
            host.LocalEndPoint, credentials: null,
            options: new TurnAllocateOptions { ReservationToken = reserved.ReservationToken });
        Assert.Equal(reserved.RelayedEndPoint.Port + 1, claimed.RelayedEndPoint.Port); // exactly the reserved odd port
    }

    [Fact]
    public async Task Even_port_and_reservation_token_together_are_rejected()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RequireAuthentication = false,
        });
        host.Start();

        var client = new TurnClient(new StunMessageCodec(), NullLogger<TurnClient>.Instance);

        await Assert.ThrowsAsync<TurnException>(() => client.AllocateAsync(
            host.LocalEndPoint,
            credentials: null,
            options: new TurnAllocateOptions { ReserveEvenPort = true, ReservationToken = 0x1234 }));
    }
}
