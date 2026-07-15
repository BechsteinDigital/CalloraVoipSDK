using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-cc delay-trend estimator: an EWMA of the per-packet delay gradients that classifies the
/// one-way delay as overusing / normal / underusing against a fixed threshold, folding each report's
/// samples in order and staying at rest for empty or zero input.
/// </summary>
public sealed class TransportCcDelayTrendEstimatorTests
{
    private static TransportCcDelaySample Sample(ushort seq, long gradientMicros) =>
        new() { SequenceNumber = seq, DelayGradientMicros = gradientMicros };

    private static void ObserveMany(TransportCcDelayTrendEstimator estimator, long gradientMicros, int count)
    {
        for (var i = 0; i < count; i++)
            estimator.Observe([Sample((ushort)i, gradientMicros)]);
    }

    [Fact]
    public void Starts_at_rest()
    {
        var estimator = new TransportCcDelayTrendEstimator(0.5, 100);

        Assert.Equal(0, estimator.TrendMicros, 6);
        Assert.Equal(CongestionSignal.Normal, estimator.Signal);
    }

    [Fact]
    public void Applies_the_ewma_weight_to_a_single_gradient()
    {
        var estimator = new TransportCcDelayTrendEstimator(0.25, 1_000);

        estimator.Observe([Sample(1, 400)]);

        Assert.Equal(100, estimator.TrendMicros, 6); // 0 × 0.75 + 400 × 0.25
    }

    [Fact]
    public void Rising_delay_trends_to_overusing()
    {
        var estimator = new TransportCcDelayTrendEstimator(0.5, 100);

        ObserveMany(estimator, gradientMicros: 200, count: 10);

        Assert.Equal(CongestionSignal.Overusing, estimator.Signal);
        Assert.True(estimator.TrendMicros > 100);
    }

    [Fact]
    public void Falling_delay_trends_to_underusing()
    {
        var estimator = new TransportCcDelayTrendEstimator(0.5, 100);

        ObserveMany(estimator, gradientMicros: -200, count: 10);

        Assert.Equal(CongestionSignal.Underusing, estimator.Signal);
        Assert.True(estimator.TrendMicros < -100);
    }

    [Fact]
    public void Recovers_from_overuse_when_the_delay_falls_again()
    {
        var estimator = new TransportCcDelayTrendEstimator(0.5, 100);

        ObserveMany(estimator, gradientMicros: 300, count: 10);
        Assert.Equal(CongestionSignal.Overusing, estimator.Signal);

        ObserveMany(estimator, gradientMicros: -300, count: 10);
        Assert.Equal(CongestionSignal.Underusing, estimator.Signal);
    }

    [Fact]
    public void Zero_gradients_stay_normal()
    {
        var estimator = new TransportCcDelayTrendEstimator(0.5, 100);

        estimator.Observe([Sample(1, 0), Sample(2, 0), Sample(3, 0)]);

        Assert.Equal(0, estimator.TrendMicros, 6);
        Assert.Equal(CongestionSignal.Normal, estimator.Signal);
    }

    [Fact]
    public void Empty_report_leaves_the_trend_unchanged()
    {
        var estimator = new TransportCcDelayTrendEstimator(0.5, 100);
        estimator.Observe([Sample(1, 500)]);
        var before = estimator.TrendMicros;

        estimator.Observe([]);

        Assert.Equal(before, estimator.TrendMicros, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Invalid_smoothing_factor_is_rejected(double smoothingFactor)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransportCcDelayTrendEstimator(smoothingFactor, 100));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_threshold_is_rejected(long threshold)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransportCcDelayTrendEstimator(0.5, threshold));
}
