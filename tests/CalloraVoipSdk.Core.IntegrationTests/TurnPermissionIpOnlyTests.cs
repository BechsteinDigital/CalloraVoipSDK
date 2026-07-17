using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 5766 §8 posture for TURN permissions (HARD-B1). Permissions are keyed by peer IP address
/// only, so a permitted address must be honoured for any source port; channel bindings (RFC 5766
/// §11) must remain keyed by the full IP:port transport address. These tests pin both halves of the
/// key split so neither collapses into the other.
/// </summary>
public sealed class TurnPermissionIpOnlyTests
{
    private static TurnServerAllocation NewAllocation() => new()
    {
        ClientKey = "client-1",
        ClientTransport = TurnServerTransport.Udp,
        RelayedTransport = TurnRequestedTransportProtocol.Udp,
        RelayedEndPoint = new IPEndPoint(IPAddress.Loopback, 50000),
        MappedEndPoint = new IPEndPoint(IPAddress.Loopback, 60000),
    };

    private static IPEndPoint Peer(string address, int port) => new(IPAddress.Parse(address), port);

    [Fact]
    public void Permission_is_honoured_for_any_source_port()
    {
        var alloc = NewAllocation();
        var now = DateTimeOffset.UtcNow;

        Assert.True(alloc.TryUpsertPermission(Peer("203.0.113.5", 4000), now.AddMinutes(5), maxPermissions: 0));

        // Same address, different source port: must pass (port-keyed permissions would reject this).
        Assert.True(alloc.HasValidPermission(Peer("203.0.113.5", 4001), now));
        Assert.True(alloc.HasValidPermission(Peer("203.0.113.5", 65000), now));
    }

    [Fact]
    public void Permission_does_not_leak_across_ip_addresses()
    {
        var alloc = NewAllocation();
        var now = DateTimeOffset.UtcNow;

        Assert.True(alloc.TryUpsertPermission(Peer("203.0.113.5", 4000), now.AddMinutes(5), maxPermissions: 0));

        // A different address is not permitted just because another address is.
        Assert.False(alloc.HasValidPermission(Peer("203.0.113.6", 4000), now));
    }

    [Fact]
    public void Permission_quota_counts_by_ip_not_port()
    {
        var alloc = NewAllocation();
        var future = DateTimeOffset.UtcNow.AddMinutes(5);

        // Two source ports of the same address collapse to a single permission slot.
        Assert.True(alloc.TryUpsertPermission(Peer("203.0.113.5", 4000), future, maxPermissions: 1));
        Assert.True(alloc.TryUpsertPermission(Peer("203.0.113.5", 4001), future, maxPermissions: 1));

        // A distinct address now exceeds the (address-counted) cap.
        Assert.False(alloc.TryUpsertPermission(Peer("203.0.113.6", 4000), future, maxPermissions: 1));
    }

    [Fact]
    public void Channel_binding_remains_port_specific()
    {
        var alloc = NewAllocation();
        var now = DateTimeOffset.UtcNow;
        var future = now.AddMinutes(5);

        Assert.True(alloc.TryUpsertChannelBinding(0x4001, Peer("203.0.113.5", 4000), future, maxChannelBindings: 0));

        // Channel bindings are full-endpoint keyed: a different port is a different peer.
        Assert.True(alloc.TryResolveChannelByPeer(Peer("203.0.113.5", 4000), now, out var boundChannel));
        Assert.Equal(0x4001, boundChannel);
        Assert.False(alloc.TryResolveChannelByPeer(Peer("203.0.113.5", 4001), now, out _));
    }
}
