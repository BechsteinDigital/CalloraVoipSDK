using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14 #8: the jitter buffer's arrival/playout clock must be monotonic so a wall-clock step (NTP,
/// manual change) cannot stall or dump playout. These assert the primitive's contract: never decreasing, and a
/// real elapsed clock (advances with time) rather than a constant.
/// </summary>
public sealed class MonotonicClockTests
{
    [Fact]
    public void Now_never_decreases_across_rapid_reads()
    {
        var previous = MonotonicClock.Now;

        for (var i = 0; i < 100_000; i++)
        {
            var current = MonotonicClock.Now;
            Assert.True(current >= previous, $"clock moved backwards on read {i}: {current:o} < {previous:o}");
            previous = current;
        }
    }

    [Fact]
    public async Task Now_advances_with_elapsed_time()
    {
        var before = MonotonicClock.Now;

        await Task.Delay(50);

        var elapsed = MonotonicClock.Now - before;
        // Lower bound only, with generous slack for scheduler jitter — the point is that it advances (not a
        // constant) and tracks real time, not that it is precise.
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(20), $"clock barely advanced: {elapsed.TotalMilliseconds} ms");
    }
}
