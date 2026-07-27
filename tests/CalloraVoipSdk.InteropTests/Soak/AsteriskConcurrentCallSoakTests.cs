using CalloraVoipSdk.InteropTests.Media;
using CalloraVoipSdk.InteropTests.Pbx;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Soak;

/// <summary>
/// Abstrakte Basis: Concurrent-Call-Soak. N parallele gebrückte Calls gegen einen PBX-Container,
/// jeder auf eigenem Endpoint-Paar, jeder mit beidseitigem RTP-Fluss. Beweist, dass SDK und PBX
/// unter Last (viele simultane Registrierungen, Dial-Requests, Bridge-Setups, Media-Sessions) stabil bleiben.
/// </summary>
public abstract class ConcurrentCallSoakMatrix
{
    protected abstract IPbxFixture CreatePbx(int bridgePairs = 1);

    /// <summary>
    /// Staggerung zwischen dem Start aufeinanderfolgender Bridged-Calls (dämpft den Thundering-Herd
    /// beim simultanen Registrierungssturm). Peer-spezifisch überschreibbar: FreeSWITCHs B2BUA ist beim
    /// gleichzeitigen Setup vieler Endpunkte empfindlicher als Asterisk und braucht mehr Puffer.
    /// </summary>
    protected virtual TimeSpan StaggerPerCall => TimeSpan.FromMilliseconds(50);

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

    private async Task RunConcurrentSoakAsync(int callCount)
    {
        // Einen einzigen PBX-Container mit callCount Bridge-Paaren:
        // Paar 0 = Basis (6001/6003), Paare 1..callCount-1 = Soak-Endpoint-Paare.
        await using var pbx = CreatePbx(callCount);
        await pbx.StartAsync();

        // Alle callCount Calls parallel starten; kleine Staggerung (StaggerPerCall/Paar) dämpft den
        // Thundering-Herd-Effekt beim simultanen Registrierungssturm gegen den PBX.
        var tasks = Enumerable.Range(0, callCount)
            .Select(i => RunOneAsync(pbx, i, staggerDelay: StaggerPerCall * i));
        var results = await Task.WhenAll(tasks);

        var failures = results.Where(r => r.Error is not null).ToArray();
        Assert.True(
            failures.Length == 0,
            $"{failures.Length}/{callCount} Bridged-Calls fehlgeschlagen:\n" +
            string.Join("\n", failures.Select(f => $"  #{f.Index}: {f.Error}")));
    }

    private static async Task<(int Index, string? Error)> RunOneAsync(
        IPbxFixture pbx, int i, TimeSpan staggerDelay)
    {
        if (staggerDelay > TimeSpan.Zero)
            await Task.Delay(staggerDelay);

        try
        {
            await using var bridged = await TwoLegBridgedCall.StartAsync(pbx, PbxMediaMode.Plain, pairIndex: i);
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

/// <summary>Fährt die Concurrent-Call-Soak-Matrix gegen einen echten Asterisk.</summary>
public sealed class AsteriskConcurrentCallSoakMatrix : ConcurrentCallSoakMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new AsteriskPbxFixture(bridgePairs);
}

/// <summary>
/// Fährt die Concurrent-Call-Soak-Matrix gegen echtes FreeSWITCH. Klassen-Trait InteropFreeSwitch,
/// damit der geerbte Short-Soak (Category=Interop) im PR-CI-Gate via Category!=InteropFreeSwitch
/// ausgeschlossen wird; die Short/Long-Methoden-Traits kommen aus der Basis.
/// </summary>
[Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchConcurrentCallSoakMatrix : ConcurrentCallSoakMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new FreeSwitchPbxFixture(bridgePairs);

    // FreeSWITCHs B2BUA verhungerte bei N=20 mit dem 50-ms-Default sporadisch einen Call im
    // simultanen 40-Endpunkt-Registrierungssturm (measure-first: 1/20 grenzwertiger Setup-Timeout).
    // Größerer Stagger dämpft den Herd → alle Legs bekommen im Setup genug Ressourcen.
    protected override TimeSpan StaggerPerCall => TimeSpan.FromMilliseconds(150);
}
