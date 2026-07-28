using System.Diagnostics;

namespace CalloraVoipSdk.InteropHarness.Perf;

/// <summary>Throughput of one benchmarked operation: operations per second over the measured window.</summary>
public readonly record struct PerfMeasurement(string Name, long Iterations, double ElapsedSeconds)
{
    /// <summary>Operations per second over the measured window.</summary>
    public double OpsPerSecond => ElapsedSeconds > 0 ? Iterations / ElapsedSeconds : 0;
}

/// <summary>
/// A minimal, allocation-light micro-benchmark runner for the media hot paths. Warms up (JIT + branch
/// prediction + cache) off the clock, then times a fixed number of operations. Used by the CORE performance
/// gate, which asserts a <b>generous</b> throughput floor: it catches catastrophic regressions (a 5–10×
/// slowdown from sync-over-async, O(n²), an allocation storm, or logging in the hot loop) without flaking on
/// the 2–3× CPU variance of shared CI runners. It is deliberately not a sensitive perf microscope.
/// </summary>
public static class PerfRunner
{
    /// <summary>
    /// Runs <paramref name="operation"/> <paramref name="warmupIterations"/> times off the clock, then
    /// <paramref name="measuredIterations"/> times on the clock, and returns the throughput.
    /// </summary>
    public static PerfMeasurement Measure(
        string name, int warmupIterations, int measuredIterations, Action operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(warmupIterations, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(measuredIterations, 1);

        for (var i = 0; i < warmupIterations; i++)
            operation();

        // Settle the heap so a mid-measurement gen-2 collection does not distort the timing.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < measuredIterations; i++)
            operation();
        sw.Stop();

        return new PerfMeasurement(name, measuredIterations, sw.Elapsed.TotalSeconds);
    }
}
