using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class RtpMediaLeakSoakTests
{
    [Fact, Trait("Category", "SoakShort")]
    public Task RepeatedLoopbackCalls_Short() => RunAsync(SoakProfile.Short);

    [Fact, Trait("Category", "SoakLong")]
    public Task RepeatedLoopbackCalls_Long() => RunAsync(SoakProfile.Long);

    private static async Task RunAsync(SoakProfile profile)
    {
        var sampler = new ResourceSampler();
        var payload = new byte[160];
        var samples = new List<ResourceSample>();

        for (var i = 0; i < profile.Iterations; i++)
        {
            await using (var loopback = await RtpMediaLoopback.StartAsync())
                _ = await loopback.RoundTripAsync(payload, TimeSpan.FromSeconds(10));

            if (i % 25 == 0)
                samples.Add(sampler.Capture());
        }
        samples.Add(sampler.Capture());

        if (samples.Count >= 5)
        {
            var trend = TrendAssertions.NoUpwardDrift(samples, s => s.ManagedBytes, toleranceRatio: 0.25);
            Assert.False(trend.HasDrift, trend.Detail);
        }
    }
}
