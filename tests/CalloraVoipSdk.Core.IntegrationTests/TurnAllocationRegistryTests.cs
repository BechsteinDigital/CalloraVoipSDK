using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Registry-wide TURN allocation admission (#155 P1-2): <see cref="TurnServerOptions.MaxTotalAllocations"/> must
/// be enforced atomically under the mutation gate, so parallel Allocate requests cannot each observe the same
/// free slot and overshoot the quota. Replacing an existing key never counts as a new allocation.
/// </summary>
public sealed class TurnAllocationRegistryTests
{
    // The sweep interval clamps to a 1s floor in RunSweepAsync; the production default (30s) would make the
    // sweep tests wait half a minute, so they pass the floor explicitly.
    private static TurnAllocationRegistry NewRegistry(int maxTotalAllocations, uint sweepIntervalSeconds = 30) => new(
        new TurnServerOptions
        {
            MaxTotalAllocations = maxTotalAllocations,
            AllocationSweepIntervalSeconds = sweepIntervalSeconds,
        },
        NullLogger.Instance,
        new TurnMobilityService(),
        new TurnTcpConnectionBroker(),
        (_, _) => Task.CompletedTask);

    private static TurnServerAllocation Allocation(string key) => new()
    {
        ClientKey = key,
        ClientTransport = TurnServerTransport.Udp,
        RelayedTransport = TurnRequestedTransportProtocol.Udp,
        RelayedEndPoint = new IPEndPoint(IPAddress.Loopback, 50000),
        MappedEndPoint = new IPEndPoint(IPAddress.Loopback, 60000),
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
    };

    [Fact]
    public async Task ReplaceAsync_enforces_MaxTotalAllocations_atomically_under_contention()
    {
        using var registry = NewRegistry(maxTotalAllocations: 1);

        // 64 concurrent inserts of distinct client keys against a cap of 1: without atomic admission several
        // would observe the free slot at once and the table would exceed the quota.
        var tasks = Enumerable.Range(0, 64)
            .Select(i => Task.Run(() => registry.ReplaceAsync(Allocation($"client-{i}"), CancellationToken.None)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(admitted => admitted));    // exactly one admitted
        Assert.Single(registry.Table);                   // the table never exceeds the cap
        Assert.Equal(63, results.Count(admitted => !admitted));  // the rest refused with a clean false

        // Replacing the admitted key stays within the cap — a refresh/replace is never counted as a new slot.
        var admittedKey = registry.Table.Keys.Single();
        Assert.True(await registry.ReplaceAsync(Allocation(admittedKey), CancellationToken.None));
        Assert.Single(registry.Table);
    }

    [Fact]
    public async Task ReplaceAsync_with_zero_cap_is_unlimited()
    {
        using var registry = NewRegistry(maxTotalAllocations: 0);   // 0 = unlimited

        for (var i = 0; i < 100; i++)
            Assert.True(await registry.ReplaceAsync(Allocation($"client-{i}"), CancellationToken.None));
        Assert.Equal(100, registry.Table.Count);
    }

    [Fact]
    public async Task RemoveInstance_is_a_no_op_when_the_key_was_replaced()
    {
        // #155 P2-2: a stale expiry sweep / TryGetLive holding an already-replaced instance must not delete the
        // newer allocation that reused the same key. RemoveInstanceAsync is reference-exact compare-and-remove.
        using var registry = NewRegistry(maxTotalAllocations: 0);
        var first = Allocation("client-1");
        Assert.True(await registry.ReplaceAsync(first, CancellationToken.None));

        var second = Allocation("client-1");   // same key, distinct instance — replaces `first`
        Assert.True(await registry.ReplaceAsync(second, CancellationToken.None));

        // Removing the stale `first` instance must leave `second` untouched.
        await registry.RemoveInstanceAsync(first);

        Assert.True(registry.TryGetLive("client-1", out var live));
        Assert.Same(second, live);
    }

    [Fact]
    public async Task TryGetLive_background_removal_of_an_expired_instance_spares_its_replacement()
    {
        // #189 expiry race: TryGetLive on an expired entry schedules a *background* removal and returns
        // false. If a refresh lands a new allocation on that key before the background task runs, an
        // instance-blind removal would delete the live replacement and silently drop the allocation.
        using var registry = NewRegistry(maxTotalAllocations: 0);
        var expired = Expired("client-1");
        Assert.True(await registry.ReplaceAsync(expired, CancellationToken.None));

        // Observing the expiry arms the background removal of THIS instance.
        Assert.False(registry.TryGetLive("client-1", out _));

        // The refresh wins the race: a live allocation takes the key while the removal is still queued.
        var refreshed = Allocation("client-1");
        Assert.True(await registry.ReplaceAsync(refreshed, CancellationToken.None));

        // Give the background removal room to run; compare-and-remove must find `expired` gone and stop.
        await Task.Delay(200);

        Assert.True(registry.TryGetLive("client-1", out var live));
        Assert.Same(refreshed, live);
    }

    [Fact]
    public async Task Sweep_reaps_expired_allocations_and_leaves_live_ones()
    {
        using var registry = NewRegistry(maxTotalAllocations: 0, sweepIntervalSeconds: 1);
        Assert.True(await registry.ReplaceAsync(Expired("expired-1"), CancellationToken.None));
        Assert.True(await registry.ReplaceAsync(Expired("expired-2"), CancellationToken.None));
        Assert.True(await registry.ReplaceAsync(Allocation("live-1"), CancellationToken.None));

        // The sweep is the primary reaper and runs independently of client traffic, so an allocation whose
        // client vanished still releases its relay port. Interval clamps to a 1s minimum.
        using var cts = new CancellationTokenSource();
        var sweep = registry.RunSweepAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (registry.Table.Count > 1 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        await cts.CancelAsync();
        await sweep;   // the loop exits cleanly on cancellation, never faults

        Assert.Equal(["live-1"], registry.Table.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Sweep_running_against_a_refresh_storm_never_reaps_the_live_instance()
    {
        // The instance-exact compare-and-remove is what makes the sweep safe under concurrent refreshes.
        // This drives both at once: each round seeds an expired instance the sweep wants to reap, then
        // immediately replaces it. Whatever the interleaving, the *live* instance must survive — the test
        // can never fail spuriously, it can only catch an instance-blind removal.
        using var registry = NewRegistry(maxTotalAllocations: 0, sweepIntervalSeconds: 1);
        using var cts = new CancellationTokenSource();
        var sweep = registry.RunSweepAsync(cts.Token);

        for (var round = 0; round < 40; round++)
        {
            var key = $"client-{round}";
            Assert.True(await registry.ReplaceAsync(Expired(key), CancellationToken.None));
            var live = Allocation(key);
            Assert.True(await registry.ReplaceAsync(live, CancellationToken.None));

            Assert.True(registry.TryGetLive(key, out var observed));
            Assert.Same(live, observed);
        }

        await cts.CancelAsync();
        await sweep;
    }

    [Fact]
    public async Task Removing_the_same_instance_twice_and_disposing_twice_are_both_no_ops()
    {
        // Teardown paths overlap (sweep, TryGetLive, explicit RemoveAsync, shutdown), so double removal and
        // double dispose have to be harmless rather than throwing on the second pass.
        var registry = NewRegistry(maxTotalAllocations: 0);
        var allocation = Allocation("client-1");
        Assert.True(await registry.ReplaceAsync(allocation, CancellationToken.None));

        await registry.RemoveInstanceAsync(allocation);
        await registry.RemoveInstanceAsync(allocation);   // already gone → compare-and-remove no-ops
        await registry.RemoveAsync("client-1");           // key gone → no-op
        Assert.Empty(registry.Table);

        await allocation.DisposeAsync();                  // the allocation itself is idempotent too
        registry.Dispose();
        registry.Dispose();
    }

    private static TurnServerAllocation Expired(string key) => new()
    {
        ClientKey = key,
        ClientTransport = TurnServerTransport.Udp,
        RelayedTransport = TurnRequestedTransportProtocol.Udp,
        RelayedEndPoint = new IPEndPoint(IPAddress.Loopback, 50001),
        MappedEndPoint = new IPEndPoint(IPAddress.Loopback, 60001),
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
    };
}
