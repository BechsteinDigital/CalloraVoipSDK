using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// First-wins TURN relay retention (#155 P1-3): only the retained (and bound) allocation yields a relay
/// candidate. A later TURN server's allocation returns <see langword="null"/> from
/// <see cref="WebRtcRelayAllocationStore.OnGathered"/>, so the gatherer advertises no unbound, unusable relay
/// candidate for it and ICE cannot nominate a dead relay path.
/// </summary>
public sealed class WebRtcRelayAllocationStoreTests
{
    private static IPEndPoint Ep(int host, int port) => new(IPAddress.Parse($"198.51.100.{host}"), port);

    private static TurnAllocateResult Alloc(int host) => new()
    {
        RelayedEndPoint = Ep(host, 50000),
        MappedEndPoint = Ep(host, 40000),
        LifetimeSeconds = 600,
    };

    [Fact]
    public void OnGathered_advertises_only_the_first_allocation_and_drops_later_servers()
    {
        var store = new WebRtcRelayAllocationStore(NullLoggerFactory.Instance);
        var hostBase = new IPEndPoint(IPAddress.Loopback, 30000);

        // First TURN server: retained and bound → advertised with the mapped base as raddr/rport.
        var first = store.OnGathered(Ep(1, 3478), Alloc(1), hostBase, () => null);
        Assert.Equal(Ep(1, 40000), first);
        Assert.Equal(Ep(1, 3478), store.Snapshot!.Value.ServerEndPoint);

        // Second TURN server: not retained (first-wins) → null, so the gatherer drops the surplus candidate.
        var second = store.OnGathered(Ep(2, 3478), Alloc(2), hostBase, () => null);
        Assert.Null(second);

        // The retained allocation is unchanged (still the first server), so its binding stays valid.
        Assert.Equal(Ep(1, 3478), store.Snapshot!.Value.ServerEndPoint);
    }

    [Fact]
    public void OnGathered_falls_back_to_the_host_base_when_no_mapped_address()
    {
        var store = new WebRtcRelayAllocationStore(NullLoggerFactory.Instance);
        var hostBase = new IPEndPoint(IPAddress.Loopback, 30000);

        var first = store.OnGathered(
            Ep(1, 3478), new TurnAllocateResult { RelayedEndPoint = Ep(1, 50000) }, hostBase, () => null);
        Assert.Equal(hostBase, first);   // no mapped address → host base
    }
}
