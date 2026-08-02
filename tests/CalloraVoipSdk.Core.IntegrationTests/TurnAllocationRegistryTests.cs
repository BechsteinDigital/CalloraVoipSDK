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
    private static TurnAllocationRegistry NewRegistry(int maxTotalAllocations) => new(
        new TurnServerOptions { MaxTotalAllocations = maxTotalAllocations },
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
}
