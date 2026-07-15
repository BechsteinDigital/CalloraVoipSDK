using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Shared transport-cc time helper: exact tick→microsecond conversion at various frequencies and,
/// crucially, without overflow for boot-relative Stopwatch magnitudes where a naive multiply wraps.
/// </summary>
public sealed class TransportCcTimeTests
{
    [Fact]
    public void Converts_ticks_to_microseconds()
    {
        Assert.Equal(1_000_000, TransportCcTime.ToMicros(1_000_000, 1_000_000)); // 1 s
        Assert.Equal(500_000, TransportCcTime.ToMicros(500_000, 1_000_000));     // 0.5 s
        Assert.Equal(6_400, TransportCcTime.ToMicros(64_000, 10_000_000));       // 64 000 ticks @ 10 MHz
    }

    [Fact]
    public void Converts_without_overflow_for_large_tick_counts()
    {
        // ~100 days of ticks at 1 GHz: a naive ticks * 1_000_000 overflows Int64; the split does not.
        const long ticks = 8_640_000_000_000_000;
        Assert.Equal(8_640_000_000_000, TransportCcTime.ToMicros(ticks, 1_000_000_000));
    }

    [Fact]
    public void Exposes_the_draft_holmer_wire_units()
    {
        Assert.Equal(64_000, TransportCcTime.ReferenceTimeUnitMicros);
        Assert.Equal(250, TransportCcTime.DeltaUnitMicros);
    }
}
