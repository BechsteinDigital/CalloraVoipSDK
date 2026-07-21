using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class RtpMediaLeakSoakTests
{
    private const int Iterations = 500;

    [Fact]
    public async Task RepeatedLoopbackCalls_DoNotDriftManagedMemoryUpward()
    {
        var sampler = new ResourceSampler();
        var payload = new byte[160];
        var samples = new List<ResourceSample>();

        for (var i = 0; i < Iterations; i++)
        {
            await using (var loopback = await RtpMediaLoopback.StartAsync())
            {
                _ = await loopback.RoundTripAsync(payload, TimeSpan.FromSeconds(10));
            }

            if (i % 25 == 0)
                samples.Add(sampler.Capture());
        }
        samples.Add(sampler.Capture());

        var result = TrendAssertions.NoUpwardDrift(
            samples, s => s.ManagedBytes, toleranceRatio: 0.25);

        Assert.False(result.HasDrift, result.Detail);
    }
}
