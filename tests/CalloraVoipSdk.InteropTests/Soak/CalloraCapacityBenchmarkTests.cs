using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;
using Xunit.Abstractions;

namespace CalloraVoipSdk.InteropTests.Soak;

/// <summary>
/// Manueller L4-Kapazitätsbenchmark für viele gleichzeitig verbundene Callora-Calls auf einer
/// registrierten SIP-Line. Jeder Call sendet PCMU und empfängt sein Asterisk-Echo. App-Frames,
/// RTP-Zähler und Frame-Abstände werden pro Richtung geprüft; das Ergebnis ist eine maschinen- und
/// profilgebundene Kapazitätshülle, kein globales SDK-Limit.
/// </summary>
public sealed class CalloraCapacityBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Erstellt den manuellen Benchmark mit xUnit-Ergebnisausgabe.</summary>
    public CalloraCapacityBenchmarkTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Prüft aufsteigende, konfigurierbare Parallelitätsstufen. Die größte Stufe, auf der jeder
    /// Call das per-direction Qualitäts-Gate besteht, und das erste instabile Ziel werden als JSON
    /// abgelegt. Der Test ist als
    /// <c>SoakLong</c> markiert und deshalb aus regulärer PR-/Release-CI ausgeschlossen.
    /// </summary>
    [DockerRequiredFact, Trait("Category", "SoakLong"), Trait("Category", "Capacity")]
    public async Task FullDuplexCalls_ReportMachineSpecificCapacityEnvelope()
    {
        var profile = CalloraCapacityProfile.FromEnvironment();
        await using var asterisk = new AsteriskContainer(
            openFileLimit: profile.AsteriskOpenFileLimit);
        await asterisk.StartAsync();

        var benchmark = new CalloraCapacityBenchmark(asterisk, profile);
        var report = await benchmark.RunAsync();

        _output.WriteLine($"Capacity report: {profile.ReportPath}");
        _output.WriteLine(
            $"Validated={report.LargestValidatedCallCount}; " +
            $"first unstable={report.FirstUnstableTarget?.ToString() ?? "none"}; " +
            $"clean teardown={report.CleanTeardown}.");

        Assert.True(
            report.LargestValidatedCallCount > 0,
            "No configured capacity level completed the per-call/per-direction quality gate.");
        Assert.True(
            report.CleanTeardown,
            $"Capacity run left {report.AsteriskChannelsAfterCleanup} Asterisk channels after cleanup.");
    }
}
