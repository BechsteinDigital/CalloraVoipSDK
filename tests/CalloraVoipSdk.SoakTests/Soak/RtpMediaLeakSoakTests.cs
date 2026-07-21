using CalloraVoipSdk.InteropHarness.Audit;
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

        // Warm-up-Phase: JIT, ThreadPool, Socket-Stack UND den Managed-Heap auf Steady-State bringen.
        // Diese Iterationen werden NICHT gemessen — sonst erfasst die Baseline den einmaligen
        // Kaltstart-Ramp (Heap/committeter Speicher wachsen beim Warmlauf) und jede Trend-/Regressions-
        // prüfung würde den Warmlauf als Leak melden. Beim Long-Profil groß genug, um den Ramp zu
        // verwerfen; beim Short-Smoke klein (dort wird ohnehin nicht gewertet).
        var warmUp = Math.Max(3, profile.Iterations / 10);
        for (var i = 0; i < warmUp; i++)
        {
            await using var w = await RtpMediaLoopback.StartAsync();
            _ = await w.RoundTripAsync(payload, TimeSpan.FromSeconds(10));
        }
        samples.Add(sampler.Capture()); // Baseline nach dem Warm-up

        for (var i = 0; i < profile.Iterations; i++)
        {
            await using (var loopback = await RtpMediaLoopback.StartAsync())
                _ = await loopback.RoundTripAsync(payload, TimeSpan.FromSeconds(10));

            if (i % 25 == 0)
                samples.Add(sampler.Capture());
        }
        samples.Add(sampler.Capture());

        // Artefakt VOR den Assertions: auch ein fehlschlagender Lauf hinterlässt seine Messreihe.
        SoakArtifactSink.TryWrite(SoakArtifactSink.CreateReport(
            "RtpMediaLeak",
            new Dictionary<string, string>
            {
                ["Iterations"] = profile.Iterations.ToString(),
                ["WarmUp"] = warmUp.ToString(),
            },
            resourceSeries: samples));

        if (samples.Count < 5)
            return; // Short-Profil: zu wenige Sockel für eine aussagekräftige Trend-/Plateau-Wertung.

        // Speicher: Steigung der Ausgleichsgeraden über die warme Reihe (robuster als Start-vs-Ende).
        // Managed-Heap settelt schnell → enge Grenze. Privater/nativer Speicher trägt einen kleinen
        // Runtime-Commit-Restramp → großzügigere Grenze; fängt grobe native Leaks (unfreie Socket-Puffer).
        var managed = TrendAssertions.NoUpwardSlope(
            samples, s => s.ManagedBytes, maxSlopePerSample: 20_000, "ManagedBytes");
        Assert.False(managed.HasDrift, managed.Detail);

        var privateMemory = TrendAssertions.NoUpwardSlope(
            samples, s => s.PrivateMemoryBytes, maxSlopePerSample: 1_000_000, "PrivateMemoryBytes");
        Assert.False(privateMemory.HasDrift, privateMemory.Detail);

        // WorkingSet wird erfasst (geht in das P2-Messreihen-Artefakt), aber nicht scharf gewertet:
        // resident memory ist OS-gesteuert und volatil — kein verlässliches Leak-Gate.

        // Zähler-Ressourcen: absolutes Plateau. Ein pro Iteration nicht freigegebener Socket/FD hebt
        // den Endsockel über hunderte Iterationen weit über die Toleranz — die scharfe Leak-Signatur.
        var threads = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.ThreadCount, absoluteTolerance: 2, "ThreadCount");
        Assert.False(threads.Exceeded, threads.Detail);

        var sockets = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.SocketDescriptorCount, absoluteTolerance: 0, "SocketDescriptorCount");
        Assert.False(sockets.Exceeded, sockets.Detail);

        var fds = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.FileDescriptorCount, absoluteTolerance: 8, "FileDescriptorCount");
        Assert.False(fds.Exceeded, fds.Detail);
    }
}
