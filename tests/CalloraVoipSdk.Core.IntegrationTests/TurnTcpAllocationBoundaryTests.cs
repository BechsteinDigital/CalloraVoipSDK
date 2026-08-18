using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The reachable boundary of the RFC 6062 TCP-allocation support through the public client (#336). A TCP
/// allocation is granted, but the client opens a fresh socket per transaction, and an RFC 8656 TCP allocation
/// is bound to its control connection's 5-tuple — so a follow-up transaction on a new socket cannot find it.
/// </summary>
/// <remarks>
/// This pins why the compliance row is "Teilweise", not "Erledigt": the server handlers (CONNECT,
/// CONNECTION-BIND, the passive CONNECTION-ATTEMPT accept loop) and the client's per-transaction primitives
/// exist and are individually correct, but the end-to-end active TCP relay is not reachable through the
/// current stateless client. Driving it would need a client that holds one control connection open across
/// Allocate → Connect → ConnectionBind — a design change, tracked, not silently claimed done.
/// </remarks>
public sealed class TurnTcpAllocationBoundaryTests
{
    [Fact]
    public async Task A_tcp_allocation_is_granted_with_a_relay_endpoint()
    {
        await using var host = TcpHost();
        host.Start();
        var client = new TurnClient(new StunMessageCodec(), NullLogger<TurnClient>.Instance);

        var allocation = await client.AllocateAsync(host.LocalEndPoint, credentials: null, options: null, transport: TurnTransport.Tcp);

        Assert.NotNull(allocation.RelayedEndPoint);
        Assert.NotEqual(0, allocation.RelayedEndPoint!.Port);
    }

    [Fact]
    public async Task A_follow_up_transaction_on_a_fresh_socket_cannot_reach_the_tcp_allocation()
    {
        // RFC 8656 §5: a TCP allocation is keyed to the control connection's 5-tuple. The stateless client
        // opens a new socket per request, so CreatePermission arrives on a 5-tuple the server has no
        // allocation for — 437 Allocation Mismatch. This is the wall the end-to-end active RFC 6062 flow hits.
        await using var host = TcpHost();
        host.Start();
        var client = new TurnClient(new StunMessageCodec(), NullLogger<TurnClient>.Instance);
        var allocation = await client.AllocateAsync(host.LocalEndPoint, credentials: null, options: null, transport: TurnTransport.Tcp);
        var peer = new IPEndPoint(IPAddress.Loopback, 40000);

        var error = await Assert.ThrowsAsync<TurnException>(() =>
            client.CreatePermissionAsync(host.LocalEndPoint, peer, allocation.EffectiveCredentials, TurnTransport.Tcp));

        Assert.Contains("437", error.Message, StringComparison.Ordinal);
    }

    private static TurnServerHost TcpHost() => new(new TurnServerHostConfiguration
    {
        BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        Transport = IceTransport.Tcp,
        RequireAuthentication = false,
    });
}
