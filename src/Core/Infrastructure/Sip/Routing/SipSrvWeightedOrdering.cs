namespace CalloraVoipSdk.Core.Infrastructure.Sip.Routing;

/// <summary>
/// Orders DNS SRV records for connection attempts per RFC 2782 (as required by RFC 3263 for SIP, CF-041):
/// records are tried in ascending priority order, and <em>within</em> one priority they are selected by a
/// weighted random draw — a record is chosen with probability proportional to its weight, so a proxy farm's
/// load is distributed across equal-priority targets rather than always hitting the highest-weight one first.
/// Weight-0 records are given only a small chance of being selected early (ordered first within the priority so
/// they win the draw only when the random target is 0).
/// </summary>
internal static class SipSrvWeightedOrdering
{
    /// <summary>
    /// Returns the records in RFC 2782 connection-attempt order: ascending priority, weighted-random within each
    /// priority group. Generic over the record type so it can be unit-tested without constructing DNS records.
    /// </summary>
    /// <typeparam name="T">The record type (e.g. a DNS <c>SrvRecord</c>).</typeparam>
    /// <param name="items">The records to order (already filtered to one service/target set).</param>
    /// <param name="priority">Reads a record's SRV priority (lower is tried first).</param>
    /// <param name="weight">Reads a record's SRV weight (relative selection likelihood within a priority).</param>
    /// <param name="nextInt">
    /// The random source: <c>nextInt(n)</c> returns an integer in <c>[0, n)</c> (e.g. <c>Random.Shared.Next</c>);
    /// injectable so the weighted draw is deterministic in tests.
    /// </param>
    public static IReadOnlyList<T> Order<T>(
        IReadOnlyList<T> items,
        Func<T, int> priority,
        Func<T, int> weight,
        Func<int, int> nextInt)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(priority);
        ArgumentNullException.ThrowIfNull(weight);
        ArgumentNullException.ThrowIfNull(nextInt);

        var result = new List<T>(items.Count);
        foreach (var group in items.GroupBy(priority).OrderBy(g => g.Key))
        {
            // RFC 2782: order the group by weight ascending (weight-0 records first) and repeatedly select a
            // record with probability proportional to its weight until the group is drained.
            var remaining = group.OrderBy(weight).ToList();
            while (remaining.Count > 0)
            {
                var total = 0;
                foreach (var record in remaining)
                    total += Math.Max(0, weight(record));

                int selectedIndex;
                if (total == 0)
                {
                    // All remaining records have weight 0: choose uniformly at random (RFC 2782).
                    selectedIndex = nextInt(remaining.Count);
                }
                else
                {
                    // Pick a target in [0, total] and select the first record whose running weight sum reaches it.
                    var target = nextInt(total + 1);
                    var running = 0;
                    selectedIndex = remaining.Count - 1;
                    for (var i = 0; i < remaining.Count; i++)
                    {
                        running += Math.Max(0, weight(remaining[i]));
                        if (running >= target)
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }

                result.Add(remaining[selectedIndex]);
                remaining.RemoveAt(selectedIndex);
            }
        }

        return result;
    }
}
