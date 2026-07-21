using System.Collections.Concurrent;
using System.Diagnostics;
using CalloraVoipSdk.InteropHarness.Audit;
using CalloraVoipSdk.InteropHarness.Diagnostics;
using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class ConcurrentLoopbackSoakTests
{
    private static readonly TimeSpan RoundTripTimeout = TimeSpan.FromSeconds(15);

    [Fact, Trait("Category", "SoakShort")]
    public Task ParallelLoopbackWaves_Short() => RunAsync(SoakProfile.Short);

    [Fact, Trait("Category", "SoakLong")]
    public Task ParallelLoopbackWaves_Long() => RunAsync(SoakProfile.Long);

    private static async Task RunAsync(SoakProfile profile)
    {
        var sampler = new ResourceSampler();
        var payload = new byte[160];
        var samples = new List<ResourceSample>();
        var failures = new ConcurrentQueue<SoakFailure>();

        // Warm-up-Wellen: ThreadPool auf das Parallelitätsniveau hochlaufen lassen und Heap/committeten
        // Speicher einschwingen, bevor die Baseline erfasst wird — sonst zählt das Plateau die einmalige
        // Pool-Expansion als Thread-Leak und die Regression den Warmlauf als Drift. Warm-up-Fehlschläge
        // werden verworfen (nicht gemessen); ein systematischer Bruch schlägt in den Messwellen ebenso durch.
        var warmUpWaves = Math.Max(2, profile.Waves / 5);
        var warmUpSink = new ConcurrentQueue<SoakFailure>();
        for (var i = 0; i < warmUpWaves; i++)
            await RunWaveAsync(wave: -(i + 1), profile.Parallelism, payload, warmUpSink);
        samples.Add(sampler.Capture());

        for (var wave = 0; wave < profile.Waves; wave++)
        {
            await RunWaveAsync(wave, profile.Parallelism, payload, failures);
            samples.Add(sampler.Capture());
        }

        // Artefakt VOR den Assertions: Messreihe + strukturierte Fehler auch bei Fehlschlag festhalten.
        SoakArtifactSink.TryWrite(SoakArtifactSink.CreateReport(
            "ConcurrentLoopback",
            new Dictionary<string, string>
            {
                ["Waves"] = profile.Waves.ToString(),
                ["Parallelism"] = profile.Parallelism.ToString(),
            },
            resourceSeries: samples, failures: failures.ToArray()));

        // Strukturierte Diagnose statt eines nackten Zählers: bei Fehlschlag zeigt die Assert-Meldung
        // Welle, Index, Portpaar, Dauer und Ausnahmetyp jedes betroffenen Round-Trips.
        Assert.True(failures.IsEmpty, SoakFailureReport.Describe(failures));

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

        var threads = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.ThreadCount, absoluteTolerance: 8, "ThreadCount");
        Assert.False(threads.Exceeded, threads.Detail);

        var sockets = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.SocketDescriptorCount, absoluteTolerance: 2, "SocketDescriptorCount");
        Assert.False(sockets.Exceeded, sockets.Detail);

        var fds = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.FileDescriptorCount, absoluteTolerance: 12, "FileDescriptorCount");
        Assert.False(fds.Exceeded, fds.Detail);
    }

    /// <summary>
    /// Startet <paramref name="parallelism"/> Loopbacks gleichzeitig, führt je einen Round-Trip aus
    /// und disposed alle wieder. Jeder Fehlschlag (Ausnahme oder falsche Länge) wird strukturiert in
    /// <paramref name="failures"/> erfasst — inkl. Welle, Index, Portpaar, verstrichener Zeit und Typ.
    /// </summary>
    private static async Task RunWaveAsync(
        int wave, int parallelism, byte[] payload, ConcurrentQueue<SoakFailure> failures)
    {
        var loopbacks = await Task.WhenAll(
            Enumerable.Range(0, parallelism).Select(_ => RtpMediaLoopback.StartAsync()));

        try
        {
            await Task.WhenAll(loopbacks.Select((loopback, index) =>
                RunOneAsync(wave, index, loopback, payload, failures)));
        }
        finally
        {
            await Task.WhenAll(loopbacks.Select(l => l.DisposeAsync().AsTask()));
        }
    }

    private static async Task RunOneAsync(
        int wave, int index, RtpMediaLoopback loopback, byte[] payload, ConcurrentQueue<SoakFailure> failures)
    {
        var ports = $"{loopback.PortPair.LegA}↔{loopback.PortPair.LegB}";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var got = await loopback.RoundTripAsync(payload, RoundTripTimeout);
            if (got.Length != payload.Length)
                failures.Enqueue(new SoakFailure(
                    wave, index, ports, stopwatch.Elapsed,
                    "LengthMismatch", $"Erwartet {payload.Length} Bytes, empfangen {got.Length}."));
        }
        catch (Exception ex)
        {
            failures.Enqueue(new SoakFailure(
                wave, index, ports, stopwatch.Elapsed, ex.GetType().Name, ex.Message));
        }
    }
}
