using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using CalloraVoipSdk.InteropTests.Media;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Soak;

/// <summary>
/// Concurrent-Call-Soak: N parallele gebrückte Calls gegen einen echten Asterisk-Container, jeder auf
/// eigenem Endpoint-Paar, jeder mit beidseitigem RTP-Fluss. Beweist, dass SDK und PBX unter Last
/// (viele simultane Registrierungen, Dial-Requests, Bridge-Setups, Media-Sessions) stabil bleiben.
/// </summary>
public sealed class AsteriskConcurrentCallSoakTests
{
    // ── Öffentliche Test-Einstiegspunkte ─────────────────────────────────────────────────────────

    /// <summary>
    /// Kurzer Soak-Smoke-Test (Kategorie "Interop"): 4 parallele Bridged-Calls (oder
    /// <c>INTEROP_SOAK_CONCURRENT_CALLS</c>). Schnell genug für die reguläre Interop-Suite.
    /// </summary>
    [DockerRequiredFact, Trait("Category", "Interop")]
    public Task ConcurrentBridgedCalls_Short_AllConnectAndFlowMedia()
        => RunConcurrentSoakAsync(CallCountFromEnv(defaultCount: 4));

    /// <summary>
    /// Langer Soak-Test (Kategorie "SoakLong"): 20 parallele Bridged-Calls (oder
    /// <c>INTEROP_SOAK_CONCURRENT_CALLS</c>). Für dedizierte Soak-Läufe; nicht in der regulären CI.
    /// </summary>
    [DockerRequiredFact, Trait("Category", "SoakLong")]
    public Task ConcurrentBridgedCalls_Long_AllConnectAndFlowMedia()
        => RunConcurrentSoakAsync(CallCountFromEnv(defaultCount: 20));

    // ── Kern-Implementierung ──────────────────────────────────────────────────────────────────────

    private static async Task RunConcurrentSoakAsync(int callCount)
    {
        // Einen einzigen Asterisk-Container mit callCount Soak-Endpoint-Paaren (sc{i}/se{i}).
        await using var asterisk = new AsteriskContainer(extraBridgePairs: callCount);
        await asterisk.StartAsync();

        // Alle callCount Calls parallel starten; kleine Staggerung (50 ms/Paar) dämpft den
        // Thundering-Herd-Effekt beim simultanen Registrierungssturm gegen Asterisk.
        var tasks = Enumerable.Range(0, callCount)
            .Select(i => RunOneAsync(asterisk, i, staggerDelay: TimeSpan.FromMilliseconds(i * 50)));
        var results = await Task.WhenAll(tasks);

        var failures = results.Where(r => r.Error is not null).ToArray();
        Assert.True(
            failures.Length == 0,
            $"{failures.Length}/{callCount} Bridged-Calls fehlgeschlagen:\n" +
            string.Join("\n", failures.Select(f => $"  #{f.Index}: {f.Error}")));
    }

    private static async Task<(int Index, string? Error)> RunOneAsync(
        AsteriskContainer asterisk, int i, TimeSpan staggerDelay)
    {
        if (staggerDelay > TimeSpan.Zero)
            await Task.Delay(staggerDelay);

        try
        {
            // Soak-Profil für Paar i: Plain RTP / PCMU, Caller=sc{i}, Callee=se{i}.
            var profile = new TwoLegProfile(
                CallerUser: asterisk.SoakCallerUser(i),
                CallerPass: asterisk.SoakPassword,
                CalleeUser: asterisk.SoakCalleeUser(i),
                CalleePass: asterisk.SoakPassword,
                BridgeExtension: asterisk.SoakBridgeExtension(i),
                SrtpPolicy: SrtpPolicy.Disabled,
                CallerCodecs: new[] { "PCMU" },
                CalleeCodecs: new[] { "PCMU" });

            await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk, profile);
            await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(8));

            // Beide Legs müssen RTP empfangen haben.
            if (bridged.CallerCall.RtpStatistics is not { PacketsReceived: > 0 })
                return (i, "Caller: kein eingehendes RTP");
            if (bridged.CalleeCall.RtpStatistics is not { PacketsReceived: > 0 })
                return (i, "Callee: kein eingehendes RTP");

            return (i, null);
        }
        catch (Exception ex)
        {
            return (i, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Hilfsmethode ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Liest die gewünschte Call-Anzahl aus der Umgebungsvariable
    /// <c>INTEROP_SOAK_CONCURRENT_CALLS</c>; fällt auf <paramref name="defaultCount"/> zurück.
    /// </summary>
    private static int CallCountFromEnv(int defaultCount)
    {
        var raw = Environment.GetEnvironmentVariable("INTEROP_SOAK_CONCURRENT_CALLS");
        return int.TryParse(raw, out var n) && n > 0 ? n : defaultCount;
    }
}
