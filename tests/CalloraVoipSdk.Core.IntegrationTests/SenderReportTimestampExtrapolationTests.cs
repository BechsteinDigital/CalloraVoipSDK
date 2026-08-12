using CalloraVoipSdk.Core.Application.Media;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-8: RFC 3550 §6.4.1 requires the SR's RTP timestamp to correspond to the same instant as its NTP
/// timestamp. Pairing current NTP with the <em>last sent</em> RTP timestamp makes that mapping stale during a
/// send pause or DTX — and that mapping is exactly what the peer uses for inter-media synchronisation, so a
/// stale one desynchronises lip-sync rather than merely being imprecise. The bundle path already extrapolated
/// onto the report instant; the single path now does the same.
/// </summary>
public sealed class SenderReportTimestampExtrapolationTests
{
    private const int ClockRate = 8000;   // 8 kHz: 8000 RTP ticks per second
    private const uint LastSent = 1_000_000;

    private static uint Extrapolate(TimeSpan? sinceLastSend, uint lastSent = LastSent, int clockRate = ClockRate)
        => CallRtcpQualityMonitor.ExtrapolateSenderReportRtpTimestamp(lastSent, sinceLastSend, clockRate);

    [Fact]
    public void A_two_second_send_pause_advances_the_timestamp_by_two_seconds_of_ticks()
    {
        // Without extrapolation the SR would carry current NTP alongside the timestamp of a packet sent two
        // seconds ago — a 16 000-tick lie about when this media was sampled.
        Assert.Equal(LastSent + 16_000u, Extrapolate(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(20, 160)]      // one 20 ms packet interval
    [InlineData(125, 1000)]    // sub-second
    [InlineData(5000, 40000)]  // a long DTX gap
    public void The_advance_is_the_elapsed_time_on_the_stream_clock(int elapsedMs, uint expectedTicks)
    {
        Assert.Equal(LastSent + expectedTicks, Extrapolate(TimeSpan.FromMilliseconds(elapsedMs)));
    }

    [Fact]
    public void An_immediate_report_reports_the_last_sent_timestamp()
    {
        Assert.Equal(LastSent, Extrapolate(TimeSpan.Zero));
    }

    [Fact]
    public void A_negative_span_clamps_to_the_last_sent_timestamp()
    {
        // Defensive: never emit a timestamp earlier than the packet it is anchored to.
        Assert.Equal(LastSent, Extrapolate(TimeSpan.FromMilliseconds(-50)));
    }

    [Fact]
    public void A_sender_without_an_elapsed_span_falls_back_to_the_last_sent_timestamp()
    {
        // The bundle-backed session reports no span through this path; the fallback is the old behaviour,
        // so this is never worse than before.
        Assert.Equal(LastSent, Extrapolate(null));
    }

    [Fact]
    public void A_zero_clock_rate_falls_back_rather_than_producing_a_meaningless_advance()
    {
        Assert.Equal(LastSent, Extrapolate(TimeSpan.FromSeconds(2), clockRate: 0));
    }

    [Fact]
    public void The_extrapolated_timestamp_wraps_like_an_rtp_timestamp()
    {
        // RFC 3550 §5.1: the RTP timestamp is 32-bit and wraps. Extrapolating past 2^32 must wrap rather than
        // clamp or throw.
        const uint nearWrap = uint.MaxValue - 4000;

        Assert.Equal(unchecked(nearWrap + 8000u), Extrapolate(TimeSpan.FromSeconds(1), lastSent: nearWrap));
    }

    [Fact]
    public void A_video_clock_rate_uses_its_own_tick_rate()
    {
        // 90 kHz video: the same wall-clock gap must advance by a different number of ticks.
        Assert.Equal(LastSent + 90_000u, Extrapolate(TimeSpan.FromSeconds(1), clockRate: 90_000));
    }
}
