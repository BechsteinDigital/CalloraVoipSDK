using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Regression for issue #8 (RFC 8656 §7.2): the server must advertise a configurable public relay
/// address in XOR-RELAYED-ADDRESS rather than silently falling back to an unreachable loopback address
/// in NAT'd / multi-host deployments. Covers the core handler override and its pass-through from the
/// public hosting facade.
/// </summary>
public sealed class TurnPublicRelayAddressTests
{
    // RFC 5737 TEST-NET-3 documentation address — never a real local interface, so a match proves the
    // advertised address came from PublicRelayAddress, not the routed-interface / loopback resolver.
    private static readonly IPAddress PublicRelay = IPAddress.Parse("203.0.113.7");

    [Fact]
    public async Task Allocate_advertises_the_configured_public_relay_address()
    {
        var codec = new StunMessageCodec();
        await using var server = new TurnServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            TurnServerTransport.Udp,
            codec,
            NullLogger<TurnServer>.Instance,
            authOptions: null,
            tlsServerCertificate: null,
            options: new TurnServerOptions { RequireAuthentication = false, PublicRelayAddress = PublicRelay });
        server.Start();

        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        var allocation = await client.AllocateAsync();

        Assert.Equal(PublicRelay, allocation.RelayedEndPoint.Address);
        Assert.NotEqual(0, allocation.RelayedEndPoint.Port);
    }

    [Fact]
    public async Task Hosted_server_advertises_the_configured_public_relay_address()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RequireAuthentication = false, // lab/test: unauthenticated allocations
            PublicRelayAddress = PublicRelay,
        });
        host.Start();

        using var client = new RawTurnUdpClient(host.LocalEndPoint, new StunMessageCodec());
        var allocation = await client.AllocateAsync();

        // Proves the full pass-through TurnServerHostConfiguration → TurnServerOptions → advertised address.
        Assert.Equal(PublicRelay, allocation.RelayedEndPoint.Address);
    }

    [Fact]
    public void Options_projection_carries_the_public_relay_address()
    {
        var options = new TurnServerHostOptions { PublicRelayAddress = PublicRelay };

        var configuration = options.ToConfiguration();

        Assert.Equal(PublicRelay, configuration.PublicRelayAddress);
    }
}
