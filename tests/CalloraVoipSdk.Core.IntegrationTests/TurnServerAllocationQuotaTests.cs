using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Enforcement gate for the per-allocation TURN quotas and background expiry (HARD-A6). A single
/// allocation must refuse permissions/channel bindings beyond its cap (486 territory) yet always
/// allow refreshes, and the sweep must reclaim capacity taken by expired entries.
/// </summary>
public sealed class TurnServerAllocationQuotaTests
{
    private static TurnServerAllocation NewAllocation() => new()
    {
        ClientKey = "client-1",
        ClientTransport = TurnServerTransport.Udp,
        RelayedTransport = TurnRequestedTransportProtocol.Udp,
        RelayedEndPoint = new IPEndPoint(IPAddress.Loopback, 50000),
        MappedEndPoint = new IPEndPoint(IPAddress.Loopback, 60000),
    };

    private static IPEndPoint Peer(int port) => new(IPAddress.Parse("203.0.113.5"), port);

    [Fact]
    public void TryUpsertPermission_refuses_new_peer_beyond_cap_but_allows_refresh()
    {
        var alloc = NewAllocation();
        var future = DateTimeOffset.UtcNow.AddMinutes(5);

        Assert.True(alloc.TryUpsertPermission(Peer(1001), future, maxPermissions: 2));
        Assert.True(alloc.TryUpsertPermission(Peer(1002), future, maxPermissions: 2));
        // A third distinct peer exceeds the cap.
        Assert.False(alloc.TryUpsertPermission(Peer(1003), future, maxPermissions: 2));
        // Refreshing an existing permission is always allowed (no new slot consumed).
        Assert.True(alloc.TryUpsertPermission(Peer(1001), future, maxPermissions: 2));
    }

    [Fact]
    public void TryUpsertChannelBinding_refuses_new_channel_beyond_cap_but_allows_rebind()
    {
        var alloc = NewAllocation();
        var future = DateTimeOffset.UtcNow.AddMinutes(5);

        Assert.True(alloc.TryUpsertChannelBinding(0x4001, Peer(2001), future, maxChannelBindings: 2));
        Assert.True(alloc.TryUpsertChannelBinding(0x4002, Peer(2002), future, maxChannelBindings: 2));
        Assert.False(alloc.TryUpsertChannelBinding(0x4003, Peer(2003), future, maxChannelBindings: 2));
        // Re-binding an existing channel number stays within the cap.
        Assert.True(alloc.TryUpsertChannelBinding(0x4001, Peer(2001), future, maxChannelBindings: 2));
    }

    [Fact]
    public void Zero_cap_means_unlimited()
    {
        var alloc = NewAllocation();
        var future = DateTimeOffset.UtcNow.AddMinutes(5);

        for (int i = 0; i < 500; i++)
            Assert.True(alloc.TryUpsertPermission(Peer(3000 + i), future, maxPermissions: 0));
    }

    [Fact]
    public void PruneExpired_reclaims_permission_capacity()
    {
        var alloc = NewAllocation();
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        var future = DateTimeOffset.UtcNow.AddMinutes(5);

        // Fill the cap with (soon-)expired permissions.
        Assert.True(alloc.TryUpsertPermission(Peer(4001), past, maxPermissions: 2));
        Assert.True(alloc.TryUpsertPermission(Peer(4002), past, maxPermissions: 2));
        // Cap full — the expired entries still occupy their slots until swept.
        Assert.False(alloc.TryUpsertPermission(Peer(4003), future, maxPermissions: 2));

        alloc.PruneExpired(DateTimeOffset.UtcNow);

        // The sweep freed the expired slots, so a new permission fits again.
        Assert.True(alloc.TryUpsertPermission(Peer(4003), future, maxPermissions: 2));
    }

    [Fact]
    public void PruneExpired_reclaims_channel_capacity_and_drops_bindings()
    {
        var alloc = NewAllocation();
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        var future = DateTimeOffset.UtcNow.AddMinutes(5);
        var now = DateTimeOffset.UtcNow;

        Assert.True(alloc.TryUpsertChannelBinding(0x4001, Peer(5001), past, maxChannelBindings: 1));
        Assert.False(alloc.TryUpsertChannelBinding(0x4002, Peer(5002), future, maxChannelBindings: 1));

        alloc.PruneExpired(now);

        // Expired binding is gone and its slot is reclaimed.
        Assert.False(alloc.TryResolvePeerByChannel(0x4001, now, out _));
        Assert.True(alloc.TryUpsertChannelBinding(0x4002, Peer(5002), future, maxChannelBindings: 1));
    }
}
