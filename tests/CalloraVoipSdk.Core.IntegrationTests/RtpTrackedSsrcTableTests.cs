using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14: the per-SSRC validator table (extracted from RtpSession) reuses a validator for a known SSRC and
/// stays bounded — capped at 64 SSRCs and LRU-evicting the least-recently-active on overflow (memory-DoS bound).
/// </summary>
public sealed class RtpTrackedSsrcTableTests
{
    [Fact]
    public void GetOrAdd_reuses_the_validator_for_a_known_ssrc()
    {
        var table = new RtpTrackedSsrcTable(NullLogger.Instance);

        var first = table.GetOrAdd(0x1111);
        var again = table.GetOrAdd(0x1111);

        Assert.Same(first, again);
        Assert.Equal(1, table.Count);
        Assert.True(table.Contains(0x1111));
    }

    [Fact]
    public void The_table_is_bounded_at_the_cap()
    {
        var table = new RtpTrackedSsrcTable(NullLogger.Instance);

        for (uint ssrc = 0; ssrc < 200; ssrc++)
            table.GetOrAdd(ssrc);

        Assert.Equal(64, table.Count); // MaxTrackedSsrcs
    }

    [Fact]
    public void Eviction_removes_the_least_recently_active_ssrc()
    {
        var table = new RtpTrackedSsrcTable(NullLogger.Instance);
        for (uint ssrc = 0; ssrc < 64; ssrc++) // fill to the cap
            table.GetOrAdd(ssrc);

        table.GetOrAdd(0);      // touch SSRC 0 → most recently active
        table.GetOrAdd(1000);   // overflow → evict the least-recently-active (SSRC 1)

        Assert.Equal(64, table.Count);
        Assert.True(table.Contains(0));     // recently touched — retained
        Assert.False(table.Contains(1));    // least-recently-active — evicted
        Assert.True(table.Contains(1000));
    }
}
