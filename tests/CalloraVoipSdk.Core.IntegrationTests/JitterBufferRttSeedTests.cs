using CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The jitter buffer seeds its RTT estimate so the adaptive delay floor has a budget before
/// the first RTCP report (avoiding early-call underruns), yet the first real RTT sample must
/// replace the seed outright so convergence stays fast on low-RTT paths (RFC 3550 §6.4.1
/// adaptive playout).
/// </summary>
public sealed class JitterBufferRttSeedTests
{
    [Fact]
    public void Default_rtt_estimate_is_seeded_for_the_pre_rtcp_window()
    {
        var buffer = new JitterBuffer();

        Assert.Equal(100, buffer.EstimatedRoundTripTimeMs);
    }

    [Fact]
    public void First_real_sample_replaces_the_seed_rather_than_smoothing_from_it()
    {
        var buffer = new JitterBuffer();

        buffer.UpdateRoundTripTime(40);

        // Fast lock: 40, not an EWMA blend of the 100 ms seed and 40.
        Assert.Equal(40, buffer.EstimatedRoundTripTimeMs);
    }

    [Fact]
    public void Later_samples_are_ewma_smoothed()
    {
        var buffer = new JitterBuffer();

        buffer.UpdateRoundTripTime(40);
        buffer.UpdateRoundTripTime(60);

        // 40 + (60 - 40) * 0.2 smoothing factor.
        Assert.Equal(44, buffer.EstimatedRoundTripTimeMs, 3);
    }

    [Fact]
    public void Explicit_seed_override_is_honored()
    {
        var buffer = new JitterBuffer(new JitterBufferOptions { InitialRoundTripTimeMs = 250 });

        Assert.Equal(250, buffer.EstimatedRoundTripTimeMs);
    }
}
