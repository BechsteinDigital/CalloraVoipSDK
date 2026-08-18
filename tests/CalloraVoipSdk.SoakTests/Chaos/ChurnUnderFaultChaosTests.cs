using CalloraVoipSdk.InteropHarness.Audit;
using CalloraVoipSdk.InteropHarness.Chaos;
using CalloraVoipSdk.InteropHarness.Metrics;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Chaos;

/// <summary>
/// CORE-011 chaos gate — Fault class 4 (resource churn under fault). Rapid connect/disconnect of media
/// sessions <b>while faults are active</b> is exactly where cleanup bugs hide: a session that never fully
/// establishes media, or a socket/task orphaned on the failure path, leaks under load. The gate asserts no
/// upward drift in managed/private memory, socket descriptors, or threads across the churn.
/// </summary>
public sealed class ChurnUnderFaultChaosTests
{
    private static readonly byte[] Payload = new byte[160];

    [Fact, Trait("Category", "Chaos")]
    public async Task Rapid_churn_under_active_faults_does_not_leak()
    {
        const int iterations = 80;
        const int warmUp = 20;
        var sampler = new ResourceSampler();
        var samples = new List<ResourceSample>();

        // Warm-up: bring JIT, ThreadPool, socket stack and the managed heap to steady state — NOT measured,
        // else the one-off cold-start ramp reads as a leak (same rationale as RtpMediaLeakSoakTests).
        for (var i = 0; i < warmUp; i++)
            await ChurnOnceAsync();

        samples.Add(Capture(sampler));

        for (var i = 0; i < iterations; i++)
        {
            await ChurnOnceAsync();
            if (i % 5 == 0)
                samples.Add(Capture(sampler));
        }
        samples.Add(Capture(sampler));

        // Artifact before the assertions: a failing run still leaves its measurement series.
        SoakArtifactSink.TryWrite(SoakArtifactSink.CreateReport(
            "ChaosChurnUnderFault",
            new Dictionary<string, string>
            {
                ["Iterations"] = iterations.ToString(),
                ["WarmUp"] = warmUp.ToString(),
            },
            resourceSeries: samples));

        // Managed heap settles fast and must come back down → tight slope bound. This is the sharp leak
        // detector, together with the thread and socket counts below.
        var managed = TrendAssertions.NoUpwardSlope(samples, s => s.ManagedBytes, 20_000, "ManagedBytes");
        Assert.False(managed.HasDrift, managed.Detail);

        // Private memory is judged on total growth, not on a slope (#283). A slope cap is a total-growth cap
        // that hides its own height: 1 MB/sample over 18 samples means "at most 18 MB", which is below what a
        // CI runner commits for the runtime, the GC heap and the ThreadPool — measured 23.5 and 24.1 MB there
        // against 7.4 MB locally, hence red and green on the same commit. This cap is explicit and roughly
        // 2.5x the worst growth measured on CI; anything that actually leaks per iteration blows past it.
        var privateMemory = TrendAssertions.NoAbsoluteGrowth(
            samples, s => s.PrivateMemoryBytes, maxGrowth: 64_000_000, "PrivateMemoryBytes");
        Assert.False(privateMemory.HasDrift, privateMemory.Detail);

        var threads = TrendAssertions.NoUpwardSlope(samples, s => s.ThreadCount, 0.5, "ThreadCount");
        Assert.False(threads.HasDrift, threads.Detail);

        // Sockets are disposed deterministically each iteration, so the descriptor count must stay flat — a
        // rising slope is a leaked socket on the fault path. Only meaningful on Linux (sentinel -1 elsewhere).
        if (samples[0].SocketDescriptorCount >= 0)
        {
            var sockets = TrendAssertions.NoUpwardSlope(samples, s => s.SocketDescriptorCount, 1.0, "SocketDescriptorCount");
            Assert.False(sockets.HasDrift, sockets.Detail);
        }
    }

    // One churn cycle: bring a faulted media session up, push a little media through the faults, tear it down.
    private static async Task ChurnOnceAsync()
    {
        await using var loop = await ChaosRtpMediaLoopback.StartAsync();
        loop.Relay.SetDropRate(0.5);
        loop.Relay.SetCorruptRate(0.2);
        await loop.SendForAsync(Payload, TimeSpan.FromMilliseconds(50));
        loop.Relay.HardFault(); // total loss for the rest of this cycle
        await loop.SendForAsync(Payload, TimeSpan.FromMilliseconds(30));
    }

    private static ResourceSample Capture(ResourceSampler sampler)
    {
        // Stabilise the reading: collect + drain finalizers so a disposed-but-not-yet-finalized object does
        // not read as drift. Deterministic disposal means sockets are already closed; this steadies memory.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return sampler.Capture();
    }
}
