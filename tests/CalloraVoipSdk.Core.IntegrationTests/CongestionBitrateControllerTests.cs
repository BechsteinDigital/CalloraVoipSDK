using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Congestion bitrate controller: an AIMD mapping from the congestion signal to a recommended video
/// bitrate — multiplicative back-off on overuse or loss, additive probe upward when healthy, clamped
/// to [min, max].
/// </summary>
public sealed class CongestionBitrateControllerTests
{
    private static CongestionBitrateController Controller() =>
        new(initialBitrateBps: 1_000_000, minBitrateBps: 100_000, maxBitrateBps: 5_000_000,
            increaseStepBps: 100_000, decreaseFactor: 0.5, lossThreshold: 0.1);

    [Fact]
    public void Overuse_backs_off_multiplicatively()
    {
        var controller = Controller();

        controller.Update(CongestionSignal.Overusing, lossRatio: 0);

        Assert.Equal(500_000, controller.TargetBitrateBps); // 1_000_000 × 0.5
    }

    [Fact]
    public void Loss_at_the_threshold_backs_off_even_when_delay_is_normal()
    {
        var controller = Controller();

        controller.Update(CongestionSignal.Normal, lossRatio: 0.2);

        Assert.Equal(500_000, controller.TargetBitrateBps);
    }

    [Fact]
    public void Healthy_network_probes_upward_additively()
    {
        var controller = Controller();

        controller.Update(CongestionSignal.Normal, lossRatio: 0);
        Assert.Equal(1_100_000, controller.TargetBitrateBps); // +100_000

        controller.Update(CongestionSignal.Underusing, lossRatio: 0);
        Assert.Equal(1_200_000, controller.TargetBitrateBps); // underuse also probes up
    }

    [Fact]
    public void Back_off_is_clamped_to_the_minimum()
    {
        var controller = Controller();

        for (var i = 0; i < 100; i++)
            controller.Update(CongestionSignal.Overusing, lossRatio: 0);

        Assert.Equal(100_000, controller.TargetBitrateBps);
    }

    [Fact]
    public void Probing_is_clamped_to_the_maximum()
    {
        var controller = Controller();

        for (var i = 0; i < 100; i++)
            controller.Update(CongestionSignal.Normal, lossRatio: 0);

        Assert.Equal(5_000_000, controller.TargetBitrateBps);
    }

    [Fact]
    public void Starts_at_the_initial_bitrate()
        => Assert.Equal(1_000_000, Controller().TargetBitrateBps);

    [Theory]
    [InlineData(0, 100_000, 5_000_000, 100_000, 0.5, 0.1)]       // min not positive
    [InlineData(1_000_000, 200_000, 100_000, 100_000, 0.5, 0.1)] // max < min
    [InlineData(50_000, 100_000, 5_000_000, 100_000, 0.5, 0.1)]  // initial below min
    [InlineData(1_000_000, 100_000, 5_000_000, 0, 0.5, 0.1)]     // increase step not positive
    [InlineData(1_000_000, 100_000, 5_000_000, 100_000, 1.0, 0.1)] // decrease factor not in (0,1)
    [InlineData(1_000_000, 100_000, 5_000_000, 100_000, 0.5, 1.5)] // loss threshold out of [0,1]
    public void Rejects_invalid_construction(
        long initial, long min, long max, long step, double decrease, double lossThreshold)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new CongestionBitrateController(initial, min, max, step, decrease, lossThreshold));
}
