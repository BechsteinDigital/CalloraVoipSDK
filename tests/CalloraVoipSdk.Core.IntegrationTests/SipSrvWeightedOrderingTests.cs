using CalloraVoipSdk.Core.Infrastructure.Sip.Routing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 2782 SRV ordering (RFC 3263 for SIP, CF-041): ascending priority across groups, and a weighted random
/// draw within each priority group (a record is chosen with probability proportional to its weight; a weight-0
/// record only wins the draw when the random target is 0). The random source is injected so the draw is
/// deterministic here.
/// </summary>
public sealed class SipSrvWeightedOrderingTests
{
    private static IReadOnlyList<string> Order(IReadOnlyList<SrvFixture> items, Func<int, int> nextInt) =>
        SipSrvWeightedOrdering.Order(items, s => s.Priority, s => s.Weight, nextInt)
            .Select(s => s.Name)
            .ToList();

    [Fact]
    public void Records_are_ordered_by_ascending_priority_across_groups()
    {
        var items = new[] { new SrvFixture(2, 5, "c"), new SrvFixture(0, 5, "a"), new SrvFixture(1, 5, "b") };

        Assert.Equal(["a", "b", "c"], Order(items, _ => 0));
    }

    [Fact]
    public void Within_a_priority_a_max_target_selects_the_higher_weight_first()
    {
        var items = new[] { new SrvFixture(0, 1, "low"), new SrvFixture(0, 9, "high") };

        Assert.Equal(["high", "low"], Order(items, n => n - 1));
    }

    [Fact]
    public void Within_a_priority_a_zero_target_selects_the_lowest_weight_first()
    {
        var items = new[] { new SrvFixture(0, 1, "low"), new SrvFixture(0, 9, "high") };

        Assert.Equal(["low", "high"], Order(items, _ => 0));
    }

    [Fact]
    public void A_weight_zero_record_only_wins_the_draw_when_the_target_is_zero()
    {
        var items = new[] { new SrvFixture(0, 5, "weighted"), new SrvFixture(0, 0, "zero") };

        Assert.Equal(["zero", "weighted"], Order(items, _ => 0));     // target 0 → the weight-0 record wins
        Assert.Equal(["weighted", "zero"], Order(items, n => n - 1)); // any positive target → the weighted one
    }

    [Fact]
    public void A_lower_priority_beats_a_higher_weight_in_another_group()
    {
        var items = new[] { new SrvFixture(1, 100, "p1"), new SrvFixture(0, 1, "p0") };

        Assert.Equal(["p0", "p1"], Order(items, n => n - 1));
    }

    [Fact]
    public void All_zero_weight_records_are_drawn_uniformly_by_the_rng()
    {
        var items = new[] { new SrvFixture(0, 0, "a"), new SrvFixture(0, 0, "b"), new SrvFixture(0, 0, "c") };
        var picks = new Queue<int>([2, 0, 0]); // pick index 2 (c), then 0 (a), then 0 (b)

        Assert.Equal(["c", "a", "b"], Order(items, _ => picks.Dequeue()));
    }
}

internal sealed record SrvFixture(int Priority, int Weight, string Name);
