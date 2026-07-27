using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14 #2: RTP SSRC / sequence-number / timestamp seeds must be drawn from the full 32-bit range with a
/// CSPRNG (RFC 3550 §8.1 / security considerations). The previous <c>Random.Shared.Next()</c> was a non-crypto
/// PRNG that never set the high bit (31-bit range) and — with <c>Next(ushort.MaxValue)</c> — never reached 65535.
/// </summary>
public sealed class RtpRandomTests
{
    // With a CSPRNG the chance a given bit is never set across N independent draws is 2^-N; at N = 2000 the
    // assertions below are deterministic for any purpose that matters (failure probability ~10^-600).
    private const int Draws = 2000;

    [Fact]
    public void NextUInt32_reaches_the_full_32_bit_range_including_the_high_bit()
    {
        var sawHighBit = false;
        var sawLowBit = false;
        var distinct = new HashSet<uint>();

        for (var i = 0; i < Draws; i++)
        {
            var value = RtpRandom.NextUInt32();
            sawHighBit |= (value & 0x8000_0000u) != 0; // the bit (uint)Random.Next() could never set
            sawLowBit |= (value & 0x0000_0001u) != 0;
            distinct.Add(value);
        }

        Assert.True(sawHighBit, "high bit (bit 31) was never set — value is not full-range");
        Assert.True(sawLowBit);
        Assert.True(distinct.Count > Draws / 2, "output is not varied — suspicious for a CSPRNG");
    }

    [Fact]
    public void NextSsrc_is_never_zero()
    {
        for (var i = 0; i < Draws; i++)
            Assert.NotEqual(0u, RtpRandom.NextSsrc());
    }

    [Fact]
    public void NextSsrc_is_never_zero_nor_the_excluded_value()
    {
        const uint excluded = 0xDEAD_BEEF;

        for (var i = 0; i < Draws; i++)
        {
            var ssrc = RtpRandom.NextSsrc(distinctFrom: excluded);
            Assert.NotEqual(0u, ssrc);
            Assert.NotEqual(excluded, ssrc);
        }
    }
}
