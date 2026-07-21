using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class ConcurrentLoopbackSoakTests
{
    [Fact, Trait("Category", "SoakShort")]
    public Task ParallelLoopbackWaves_Short() => RunAsync(SoakProfile.Short);

    [Fact, Trait("Category", "SoakLong")]
    public Task ParallelLoopbackWaves_Long() => RunAsync(SoakProfile.Long);

    private static async Task RunAsync(SoakProfile profile)
    {
        var sampler = new ResourceSampler();
        var payload = new byte[160];
        var samples = new List<ResourceSample>();
        var failures = 0;

        for (var wave = 0; wave < profile.Waves; wave++)
        {
            var loopbacks = await Task.WhenAll(
                Enumerable.Range(0, profile.Parallelism).Select(_ => RtpMediaLoopback.StartAsync()));

            try
            {
                var results = await Task.WhenAll(loopbacks.Select(async l =>
                {
                    try
                    {
                        var got = await l.RoundTripAsync(payload, TimeSpan.FromSeconds(15));
                        return got.Length == payload.Length;
                    }
                    catch
                    {
                        return false;
                    }
                }));

                failures += results.Count(ok => !ok);
            }
            finally
            {
                await Task.WhenAll(loopbacks.Select(l => l.DisposeAsync().AsTask()));
            }

            samples.Add(sampler.Capture());
        }

        Assert.Equal(0, failures);

        if (samples.Count >= 5)
        {
            var trend = TrendAssertions.NoUpwardDrift(
                samples, s => s.ManagedBytes, toleranceRatio: 0.30);
            Assert.False(trend.HasDrift, trend.Detail);
        }
    }
}
