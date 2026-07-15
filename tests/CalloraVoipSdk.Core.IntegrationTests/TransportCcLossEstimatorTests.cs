using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-cc loss estimator: a smoothed lost-to-reported ratio from feedback reports — zero when
/// all arrive, weighted by the EWMA factor per report, unchanged for an empty report.
/// </summary>
public sealed class TransportCcLossEstimatorTests
{
    private static TransportCcFeedbackResult Received(ushort seq) =>
        new() { SequenceNumber = seq, Received = true };

    private static TransportCcFeedbackResult Lost(ushort seq) =>
        new() { SequenceNumber = seq, Received = false };

    [Fact]
    public void Starts_at_zero()
    {
        Assert.Equal(0, new TransportCcLossEstimator(0.5).LossRatio, 6);
    }

    [Fact]
    public void All_received_is_zero_loss()
    {
        var estimator = new TransportCcLossEstimator(1.0);

        estimator.Observe([Received(1), Received(2), Received(3)]);

        Assert.Equal(0, estimator.LossRatio, 6);
    }

    [Fact]
    public void Reports_the_lost_fraction_at_full_weight()
    {
        var estimator = new TransportCcLossEstimator(1.0); // fully reactive

        estimator.Observe([Received(1), Lost(2), Received(3), Lost(4)]); // 2 of 4 lost

        Assert.Equal(0.5, estimator.LossRatio, 6);
    }

    [Fact]
    public void Applies_the_ewma_weight()
    {
        var estimator = new TransportCcLossEstimator(0.25);

        estimator.Observe([Lost(1)]); // 100 % loss this report

        Assert.Equal(0.25, estimator.LossRatio, 6); // 0 × 0.75 + 1.0 × 0.25
    }

    [Fact]
    public void Converges_toward_a_steady_loss_rate()
    {
        var estimator = new TransportCcLossEstimator(0.5);

        for (var i = 0; i < 20; i++)
            estimator.Observe([Received(1), Lost(2)]); // 50 % each report

        Assert.Equal(0.5, estimator.LossRatio, 3);
    }

    [Fact]
    public void Empty_report_leaves_the_ratio_unchanged()
    {
        var estimator = new TransportCcLossEstimator(0.5);
        estimator.Observe([Lost(1)]);
        var before = estimator.LossRatio;

        estimator.Observe([]);

        Assert.Equal(before, estimator.LossRatio, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Invalid_smoothing_factor_is_rejected(double smoothingFactor)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TransportCcLossEstimator(smoothingFactor));
}
