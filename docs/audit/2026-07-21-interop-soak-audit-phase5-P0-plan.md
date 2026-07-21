# Phase 5 / P0 — CI-Soak-Trennung (Merge-Blocker): Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** Den Merge-Blocker beheben: die schweren Soaks (500 Zyklen, 20×25 parallel, 20 s Media, 20 s REGISTER) dürfen NICHT in jedem PR-CI-Lauf laufen. Struktur: `SoakShort` (schnell, PR-CI) vs. `SoakLong` (schwer, nightly), Env-konfigurierbare Parameter, Interop gegated, `ci.yml`-Filter + neuer `soak.yml`.

**Architecture:** Zentrale `SoakProfile` liest Iterationen/Wellen/Parallelität/Dauer aus Env-Vars (Short-Defaults klein, Long-Defaults groß + override-bar). Jeder Soak-Fokus bekommt eine gemeinsame parametrisierte Kern-Methode + zwei Entry-Points: `_Short` (`[Trait("Category","SoakShort")]`) und `_Long` (`[Trait("Category","SoakLong")]`). Interop-Tests bekommen `[Trait("Category","Interop")]`. PR-CI filtert `Category!=SoakLong&Category!=Interop`; `soak.yml` läuft nur `Category=SoakLong`; ein Ubuntu-Interop-Job läuft `Category=Interop`.

**Tech Stack:** .NET 8/9/10, xUnit Traits, GitHub Actions.

---

## Verifizierte Fakten
- `ci.yml:44`: `dotnet test CalloraVoipSdk.sln --configuration Release --no-build --filter "FullyQualifiedName!~CalloraVoipSdk.Core.Tests" ...` auf `ubuntu-latest`+`windows-latest`, net8/9/10 → SoakTests+InteropTests laufen mit.
- Schwere Soaks heute: `RtpMediaLeakSoakTests` (Iterations=500), `ConcurrentLoopbackSoakTests` (Waves=20, Parallelism=25), `MediaQualityDriftSoakTests` (20 s + geskippter F002-Loss), `SignalingRefreshSoakTests` (20 s).
- Schnelle Tests (kein Trait, laufen weiter in PR-CI): `RtpMediaLoopbackRoundTripTests`, `RtpMediaLoopbackParallelStartTests`, `TrendAssertions*Tests`, `AuditFindingFormatterTests`, `SmokeTests`, `SipRegisterLoopHarnessTests`.
- SoakTests-Assembly ist serialisiert (`[assembly: CollectionBehavior(DisableTestParallelization=true)]`).
- Interop-Tests: `AsteriskContainerSmokeTests`, `AsteriskRegisterInteropTests` (Docker, Linux-only Bridge-IP).

---

## Task 1: `SoakProfile` + SoakShort/SoakLong-Umbau der Soaks

**Files:**
- Create: `tests/CalloraVoipSdk.SoakTests/SoakProfile.cs`
- Modify: `tests/CalloraVoipSdk.SoakTests/Soak/RtpMediaLeakSoakTests.cs`, `ConcurrentLoopbackSoakTests.cs`, `SignalingRefreshSoakTests.cs`, `Soak/MediaQualityDriftSoakTests.cs`

- [ ] **Step 1: `SoakProfile`** — `tests/CalloraVoipSdk.SoakTests/SoakProfile.cs`:

```csharp
namespace CalloraVoipSdk.SoakTests;

/// <summary>
/// Parametersatz für einen Soak-Lauf. <see cref="Short"/> liefert kleine, CI-taugliche Werte
/// (Smoke: läuft der Mechanismus?); <see cref="Long"/> die schweren Werte für den nightly-Lauf,
/// per Umgebungsvariable übersteuerbar (<c>SOAK_ITERATIONS</c>, <c>SOAK_WAVES</c>,
/// <c>SOAK_PARALLELISM</c>, <c>SOAK_DURATION_SECONDS</c>).
/// </summary>
/// <param name="Iterations">Anzahl serieller Zyklen (Leak-Soak).</param>
/// <param name="Waves">Anzahl paralleler Wellen (Concurrency-Soak).</param>
/// <param name="Parallelism">Parallele Loopbacks pro Welle.</param>
/// <param name="Duration">Lauf-Dauer (Media-/Signaling-Soak).</param>
public sealed record SoakProfile(int Iterations, int Waves, int Parallelism, TimeSpan Duration)
{
    /// <summary>Kleines PR-CI-Profil (Smoke) — schnell, deckt den Mechanismus ab, nicht die Leak-Tiefe.</summary>
    public static SoakProfile Short { get; } = new(
        Iterations: 20, Waves: 3, Parallelism: 5, Duration: TimeSpan.FromSeconds(3));

    /// <summary>Schweres nightly-Profil (echte Leak-/Drift-Tiefe), per Env-Var übersteuerbar.</summary>
    public static SoakProfile Long => new(
        Iterations: EnvInt("SOAK_ITERATIONS", 500),
        Waves: EnvInt("SOAK_WAVES", 20),
        Parallelism: EnvInt("SOAK_PARALLELISM", 25),
        Duration: TimeSpan.FromSeconds(EnvInt("SOAK_DURATION_SECONDS", 20)));

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
```

- [ ] **Step 2: Leak-Soak umbauen** — `RtpMediaLeakSoakTests.cs`: replace the single `[Fact]` with a shared core + two entry-points. The core takes `SoakProfile`; the trend assertion only runs when there are enough samples (Short may have too few — then just assert no exceptions). Concretely:

```csharp
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

        for (var i = 0; i < profile.Iterations; i++)
        {
            await using (var loopback = await RtpMediaLoopback.StartAsync())
                _ = await loopback.RoundTripAsync(payload, TimeSpan.FromSeconds(10));

            if (i % 25 == 0)
                samples.Add(sampler.Capture());
        }
        samples.Add(sampler.Capture());

        // Trend nur bei genügend Samples aussagekräftig (Short ist ein Mechanik-Smoke).
        if (samples.Count >= 5)
        {
            var trend = TrendAssertions.NoUpwardDrift(samples, s => s.ManagedBytes, toleranceRatio: 0.25);
            Assert.False(trend.HasDrift, trend.Detail);
        }
    }
}
```

- [ ] **Step 3: Concurrency-Soak umbauen** — `ConcurrentLoopbackSoakTests.cs`: same pattern; core takes `profile.Waves`/`profile.Parallelism`. Keep the `failures` count + trend (trend only if `samples.Count >= 5`). Two entry-points `_Short`/`_Long` with the two Traits.

- [ ] **Step 4: Media-Drift umbauen** — `MediaQualityDriftSoakTests.cs`: the green `LongCall_JitterDoesNotDrift` gets a core taking `profile.Duration`; two entry-points `_Short` (3 s) / `_Long` (20 s) with Traits. The `[Fact(Skip="F002")]` loss test keeps its skip and gets `[Trait("Category","SoakLong")]` (it's a long-form test).

- [ ] **Step 5: Signaling-Soak umbauen** — `SignalingRefreshSoakTests.cs`: core takes `profile.Duration`; `_Short` (~6 s) / `_Long` (20 s) with Traits. (Note: the ≥5-cycles assertion must scale — for Short use `>= 2`.)

- [ ] **Step 6: Run both categories locally to confirm split:**
  - Short only (fast): `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter "Category=SoakShort"` → green, fast (seconds).
  - Long excluded from PR filter: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter "Category!=SoakLong"` → runs Short + the untagged fast tests, NOT the heavy Long ones. Confirm the heavy soaks did NOT run (fast wall-clock).
  - `Category=SoakLong` alone → the heavy ones run (slow). One quick confirmation is enough.

- [ ] **Step 7: Commit:**
```bash
git add tests/CalloraVoipSdk.SoakTests/
git commit -m "$(cat <<'MSG'
feat(interop-soak): P0.1 — SoakShort/SoakLong-Trennung + Env-konfigurierbares SoakProfile

Schwere Soaks laufen nur noch als SoakLong (nightly); SoakShort-Smokes im PR-CI.
Iterationen/Wellen/Parallelität/Dauer über SOAK_*-Env-Vars übersteuerbar.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 2: Interop-Tests gaten

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainerSmokeTests.cs`, `Registration/AsteriskRegisterInteropTests.cs`

- [ ] **Step 1:** Add `[Trait("Category", "Interop")]` to both interop test classes (class-level trait applies to all methods). They already use `[DockerRequiredFact]`; the trait lets the PR filter exclude them (they're Docker+Linux-only, slow).

- [ ] **Step 2:** Confirm: `dotnet test tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj -f net10.0 --filter "Category!=Interop"` → 0 tests run (all are Interop). And `--filter "Category=Interop"` → the 2 run.

- [ ] **Step 3: Commit:**
```bash
git add tests/CalloraVoipSdk.InteropTests/
git commit -m "$(cat <<'MSG'
feat(interop-soak): P0.2 — Interop-Tests als Category=Interop gegated (Docker/Linux, nicht im PR-CI-Matrix)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 3: PR-CI-Filter anpassen + `soak.yml` + Interop-Job

**Files:**
- Modify: `.github/workflows/ci.yml`
- Create: `.github/workflows/soak.yml`

- [ ] **Step 1:** In `ci.yml`, change the test filter (line 44) to exclude SoakLong + Interop:
`--filter "FullyQualifiedName!~CalloraVoipSdk.Core.Tests&Category!=SoakLong&Category!=Interop"`
Leave everything else unchanged.

- [ ] **Step 2:** Add an Ubuntu-only interop job to `ci.yml` (Docker available on ubuntu-latest GitHub runners; Windows runners can't run Linux containers). Append a second job:

```yaml
  interop:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x
      - name: Restore
        run: dotnet restore CalloraVoipSdk.sln
      - name: Build
        run: dotnet build CalloraVoipSdk.sln --configuration Release --no-restore
      - name: Interop tests (Docker/Asterisk)
        run: dotnet test tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj --configuration Release --no-build -f net10.0 --filter "Category=Interop" --nologo --verbosity minimal
```

- [ ] **Step 3:** Create `.github/workflows/soak.yml` (nightly + manual, SoakLong only, Ubuntu, one TFM to keep it lean):

```yaml
name: soak

on:
  workflow_dispatch:
    inputs:
      iterations:
        description: "SOAK_ITERATIONS"
        required: false
        default: "500"
      duration_seconds:
        description: "SOAK_DURATION_SECONDS"
        required: false
        default: "20"
  schedule:
    - cron: "0 3 * * *"  # täglich 03:00 UTC

permissions:
  contents: read

jobs:
  soak-long:
    runs-on: ubuntu-latest
    env:
      SOAK_ITERATIONS: ${{ github.event.inputs.iterations || '500' }}
      SOAK_DURATION_SECONDS: ${{ github.event.inputs.duration_seconds || '20' }}
    steps:
      - name: Checkout
        uses: actions/checkout@v4
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x
      - name: Restore
        run: dotnet restore CalloraVoipSdk.sln
      - name: Build
        run: dotnet build CalloraVoipSdk.sln --configuration Release --no-restore
      - name: Long soaks
        run: dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj --configuration Release --no-build -f net10.0 --filter "Category=SoakLong" --nologo --verbosity minimal
```

- [ ] **Step 4:** Sanity — the workflow YAML is valid (indentation, keys). Optionally validate with a YAML linter if available; otherwise careful visual check. Note: cannot run GitHub Actions locally; the filters were validated in Task 1/2.

- [ ] **Step 5: Commit:**
```bash
git add .github/workflows/ci.yml .github/workflows/soak.yml
git commit -m "$(cat <<'MSG'
feat(interop-soak): P0.3 — PR-CI schließt SoakLong+Interop aus; nightly soak.yml + Ubuntu-Interop-Job

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## P0-Abschluss
- [ ] Voller Build → 0/0. · `dotnet test SoakTests -f net10.0 --filter "Category!=SoakLong"` läuft SCHNELL (kein 500-Iter/20 s-Soak). · `--filter "Category=SoakLong"` läuft die schweren einmal grün.
- [ ] `origin/main`-Check + ggf. mergen.

**Damit ist der Merge-Blocker weg.** Danach P1a (Ressourcen-Metriken) → P1b (Regression) → P1c (Concurrency-Diagnostik) → P1d (Media-Matrix) → P1e (Signaling-Benennung) → P2 (Audit-Sink).

## Spec-Abdeckung (Self-Review)
- P0 (CI-Soak-Last) → Task 1–3 vollständig. Env-Var-Config → Task 1 (`SoakProfile`).
- Kein Verhaltensverlust: die bestehende Soak-Logik bleibt (als `_Long`); `_Short` ist zusätzlich.
