using CalloraVoipSdk.InteropHarness.Audit;
using CalloraVoipSdk.InteropHarness.Perf;
using Xunit;
using Xunit.Abstractions;

namespace CalloraVoipSdk.SoakTests.Perf;

/// <summary>
/// CORE performance gate — the SRTP per-packet crypto hot path, which runs on <b>every</b> media packet of
/// <b>every</b> call, both directions, so a regression here scales with the whole server's load. Asserts a
/// GENEROUS throughput floor (~7–8× below the locally measured Debug throughput): a slow CI runner never
/// flakes, but a catastrophic regression (sync-over-async, O(n²), an allocation storm, logging in the hot
/// loop — typically a 10×+ slowdown) fails the gate. It is deliberately not a sensitive perf microscope; the
/// measured ops/s is emitted as an artifact for manual trend review.
/// </summary>
public sealed class SrtpPerfGateTests
{
    private readonly ITestOutputHelper _output;

    public SrtpPerfGateTests(ITestOutputHelper output) => _output = output;

    // Floors are ~13% of the locally measured Debug throughput (AES-CM ~116k/s, GCM ~514k/s). CI runs Release
    // (faster), so the effective margin is even larger. A regression must drop throughput ~7×+ to trip these.
    private const double AesCmFloorOpsPerSec = 15_000;
    private const double GcmFloorOpsPerSec = 60_000;

    [Fact, Trait("Category", "Perf")]
    public void Srtp_aes_cm_protect_stays_above_the_catastrophic_regression_floor() =>
        AssertFloor(MediaCryptoBenchmarks.SrtpProtectAesCm128(), AesCmFloorOpsPerSec);

    [Fact, Trait("Category", "Perf")]
    public void Srtp_gcm_protect_stays_above_the_catastrophic_regression_floor() =>
        AssertFloor(MediaCryptoBenchmarks.SrtpProtectAeadGcm128(), GcmFloorOpsPerSec);

    private void AssertFloor(PerfMeasurement m, double floorOpsPerSec)
    {
        _output.WriteLine(
            $"{m.Name}: {m.OpsPerSecond:N0} ops/s (floor {floorOpsPerSec:N0}); {m.Iterations:N0} ops in {m.ElapsedSeconds:F3}s");

        // Artifact for manual trend review (best-effort; a no-op unless the soak artifact dir env is set).
        SoakArtifactSink.TryWrite(SoakArtifactSink.CreateReport(
            $"Perf.{m.Name}",
            new Dictionary<string, string>
            {
                ["OpsPerSecond"] = ((long)m.OpsPerSecond).ToString(),
                ["FloorOpsPerSecond"] = ((long)floorOpsPerSec).ToString(),
                ["Iterations"] = m.Iterations.ToString(),
            }));

        Assert.True(
            m.OpsPerSecond >= floorOpsPerSec,
            $"{m.Name} throughput {m.OpsPerSecond:N0} ops/s fell below the catastrophic-regression floor {floorOpsPerSec:N0} ops/s.");
    }
}
