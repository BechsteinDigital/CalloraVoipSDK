using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Hosting;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Proves the public <see cref="TurnServerHost"/> facade produces a real, serving TURN server: a host built from
/// configuration binds, starts, and answers an Allocate over the wire (via <see cref="RawTurnUdpClient"/>) with a
/// relayed endpoint — the same round-trip <c>TurnServerE2eTests</c> exercises on the internal server, but reached
/// through the public hosting facade.
/// </summary>
public sealed class TurnServerHostE2eTests
{
    [Fact]
    public async Task The_hosted_turn_server_serves_an_allocation_end_to_end()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RequireAuthentication = false, // lab/test: unauthenticated allocations
        });
        host.Start();

        using var client = new RawTurnUdpClient(host.LocalEndPoint, new StunMessageCodec());
        var allocation = await client.AllocateAsync();

        Assert.NotNull(allocation.RelayedEndPoint);
        Assert.Equal(IPAddress.Loopback, allocation.RelayedEndPoint.Address);
        Assert.NotEqual(0, allocation.RelayedEndPoint.Port);
        Assert.True(allocation.LifetimeSeconds > 0, "the hosted server must grant a positive allocation lifetime");
    }
}
