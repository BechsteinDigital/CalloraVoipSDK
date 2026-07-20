using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RTCP transmission-interval calculator (CF-006, RFC 3550 §6.2/§6.3.1): a member-scaled deterministic
/// interval, floored at Tmin, randomised over [0.5, 1.5] and divided by the compensation factor e−1.5 so the
/// long-run mean is preserved. The first report halves Tmin. Verified with an injected randomness source.
/// </summary>
public sealed class RtcpTransmissionIntervalTests
{
    private static readonly TimeSpan Tmin = TimeSpan.FromSeconds(5);
    private const double Compensation = 1.21828;

    [Fact]
    public void Mid_random_divides_the_floored_interval_by_the_compensation_factor()
    {
        // 1:1 bundle: the deterministic term (2·128B·8 / 5000 ≈ 0.41 s) is below Tmin, so Tmin floors it.
        // random 0.5 → factor (0.5+0.5)=1.0 → 5 s / 1.21828.
        var calc = new RtcpTransmissionInterval(Tmin, rtcpBandwidthBitsPerSecond: 5000, random: () => 0.5);
        var t = calc.Compute(members: 2, senders: 1, weSent: true, averageRtcpSizeBytes: 100, initial: false);
        Assert.Equal(5.0 / Compensation, t.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Random_spans_half_to_one_and_a_half_of_the_floored_interval()
    {
        var low = new RtcpTransmissionInterval(Tmin, 5000, random: () => 0.0)
            .Compute(2, 1, weSent: true, averageRtcpSizeBytes: 100, initial: false);
        var high = new RtcpTransmissionInterval(Tmin, 5000, random: () => 0.999)
            .Compute(2, 1, weSent: true, averageRtcpSizeBytes: 100, initial: false);

        Assert.Equal(5.0 * 0.5 / Compensation, low.TotalSeconds, precision: 3);   // ≈ 2.05 s
        Assert.Equal(5.0 * 1.499 / Compensation, high.TotalSeconds, precision: 2); // ≈ 6.15 s
    }

    [Fact]
    public void The_first_report_halves_the_minimum_floor()
    {
        var calc = new RtcpTransmissionInterval(Tmin, 5000, random: () => 0.5);
        var t = calc.Compute(members: 2, senders: 0, weSent: false, averageRtcpSizeBytes: 100, initial: true);
        Assert.Equal(2.5 / Compensation, t.TotalSeconds, precision: 3); // half Tmin, mid random
    }

    [Fact]
    public void Large_membership_scales_the_interval_far_above_the_floor()
    {
        // 2000 senders share the RTCP bandwidth: 2000·128B·8 / 5000 ≈ 409.6 s, well above Tmin.
        var calc = new RtcpTransmissionInterval(Tmin, 5000, random: () => 0.5);
        var t = calc.Compute(members: 2000, senders: 2000, weSent: true, averageRtcpSizeBytes: 100, initial: false);
        Assert.True(t.TotalSeconds > 60, $"expected the interval to scale up with membership, got {t.TotalSeconds:F1} s");
    }

    [Fact]
    public void Rejects_non_positive_configuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RtcpTransmissionInterval(TimeSpan.Zero, 5000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RtcpTransmissionInterval(Tmin, 0));
    }
}
