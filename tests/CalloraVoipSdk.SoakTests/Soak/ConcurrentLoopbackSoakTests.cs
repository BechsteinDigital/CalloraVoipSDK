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

        // Warm-up-Wellen: ThreadPool auf das Parallelitätsniveau hochlaufen lassen und Heap/committeten
        // Speicher einschwingen, bevor die Baseline erfasst wird — sonst zählt das Plateau die einmalige
        // Pool-Expansion als Thread-Leak und die Regression den Warmlauf als Drift.
        var warmUpWaves = Math.Max(2, profile.Waves / 5);
        for (var i = 0; i < warmUpWaves; i++)
            _ = await RunWaveAsync(profile.Parallelism, payload);
        samples.Add(sampler.Capture());

        for (var wave = 0; wave < profile.Waves; wave++)
        {
            failures += await RunWaveAsync(profile.Parallelism, payload);
            samples.Add(sampler.Capture());
        }

        Assert.Equal(0, failures);

        if (samples.Count < 5)
            return; // Short-Profil: zu wenige Wellen für eine aussagekräftige Wertung.

        // Bursty-Concurrency ist speicher-volatiler als der serielle Leak-Soak → großzügigere
        // Steigungsgrenzen, aber weiterhin Regression (absolute Steigung) statt relativem %-Wachstum.
        var managed = TrendAssertions.NoUpwardSlope(
            samples, s => s.ManagedBytes, maxSlopePerSample: 60_000, "ManagedBytes");
        Assert.False(managed.HasDrift, managed.Detail);

        var privateMemory = TrendAssertions.NoUpwardSlope(
            samples, s => s.PrivateMemoryBytes, maxSlopePerSample: 2_000_000, "PrivateMemoryBytes");
        Assert.False(privateMemory.HasDrift, privateMemory.Detail);

        // ThreadPool wächst unter bursty Last per Hill-Climbing weiter, auch nach dem Warm-up →
        // großzügigere absolute Thread-Toleranz, aber weiterhin ein Plateau (kein relatives %-Wachstum).
        var threads = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.ThreadCount, absoluteTolerance: 8, "ThreadCount");
        Assert.False(threads.Exceeded, threads.Detail);

        // Sockets werden je Welle nach dem Dispose gemessen → müssen auf das Baseline-Niveau zurückfallen.
        var sockets = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.SocketDescriptorCount, absoluteTolerance: 2, "SocketDescriptorCount");
        Assert.False(sockets.Exceeded, sockets.Detail);

        var fds = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.FileDescriptorCount, absoluteTolerance: 12, "FileDescriptorCount");
        Assert.False(fds.Exceeded, fds.Detail);
    }

    /// <summary>
    /// Startet <paramref name="parallelism"/> Loopbacks gleichzeitig, führt je einen Round-Trip aus
    /// und disposed alle wieder. Liefert die Anzahl fehlgeschlagener Round-Trips dieser Welle.
    /// </summary>
    private static async Task<int> RunWaveAsync(int parallelism, byte[] payload)
    {
        var loopbacks = await Task.WhenAll(
            Enumerable.Range(0, parallelism).Select(_ => RtpMediaLoopback.StartAsync()));

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

            return results.Count(ok => !ok);
        }
        finally
        {
            await Task.WhenAll(loopbacks.Select(l => l.DisposeAsync().AsTask()));
        }
    }
}
