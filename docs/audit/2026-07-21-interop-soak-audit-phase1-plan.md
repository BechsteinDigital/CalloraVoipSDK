# Phase 1 — Concurrency-Soak: Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Den Last-/Concurrency-Soak-Fokus abdecken — viele `RtpMediaLoopback`-Instanzen gleichzeitig, Round-Trips erfolgreich, Ressourcen stabil — und dabei den `FreeUdpPort`-TOCTOU-Nit aus dem Phase-0-Review schließen (bounded Retry bei Port-Bind-Kollision).

**Architecture:** Erweitert die bestehende L2-Fixture `RtpMediaLoopback` (Phase 0) um TOCTOU-robuste Erzeugung und fügt einen Concurrency-Soak in `SoakTests` hinzu, der `ResourceSampler`/`TrendAssertions` (Phase 0.2) wiederverwendet.

**Tech Stack:** .NET 8/9/10, xUnit 2.4.2, bestehende Harness-Primitiven.

---

## Verifizierte Fakten (Ground-Truth)

- `RtpMediaLoopback.StartAsync()` (Phase 0.4, exception-safe Factory) erzeugt zwei `RtpCallMediaSession`; **die UDP-Sockets werden im Ctor gebunden** (nicht in `StartAsync`) — eine Port-Kollision wirft daher bei `new RtpCallMediaSession(...)`.
- Bind-Kollision → `System.Net.Sockets.SocketException` mit `SocketError.AddressAlreadyInUse` (aus `Socket.Bind`).
- `FreeUdpPort()` bindet einen Probe-Socket auf Port 0, liest den Port, schließt ihn — TOCTOU-Fenster bis zum echten Bind. Unter Parallelität real.
- `ResourceSampler.Capture()` / `TrendAssertions.NoUpwardDrift(...)` aus Phase 0.2 verfügbar.

---

## File Structure

```text
tests/CalloraVoipSdk.InteropHarness/Media/RtpMediaLoopback.cs   MODIFY: TOCTOU-robuste StartAsync (bounded Retry)
tests/CalloraVoipSdk.SoakTests/Media/RtpMediaLoopbackParallelStartTests.cs   NEW: Parallel-Erzeugungs-Stresstest
tests/CalloraVoipSdk.SoakTests/Soak/ConcurrentLoopbackSoakTests.cs           NEW: Wellen paralleler Round-Trips + Trend
```

---

## Task 1: TOCTOU-robuste `StartAsync` + Parallel-Stresstest

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropHarness/Media/RtpMediaLoopback.cs`
- Test: `tests/CalloraVoipSdk.SoakTests/Media/RtpMediaLoopbackParallelStartTests.cs`

- [ ] **Step 1: Failing parallel-start test** — `tests/CalloraVoipSdk.SoakTests/Media/RtpMediaLoopbackParallelStartTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Media;

namespace CalloraVoipSdk.SoakTests.Media;

public sealed class RtpMediaLoopbackParallelStartTests
{
    [Fact]
    public async Task StartAsync_ManyInParallel_AllSucceedWithoutPortCollision()
    {
        // Startet viele Fixtures gleichzeitig — provoziert das FreeUdpPort-TOCTOU-Fenster.
        const int parallelism = 40;

        var started = await Task.WhenAll(
            Enumerable.Range(0, parallelism).Select(_ => RtpMediaLoopback.StartAsync()));

        try
        {
            Assert.Equal(parallelism, started.Length);
            Assert.All(started, Assert.NotNull);
        }
        finally
        {
            await Task.WhenAll(started.Select(l => l.DisposeAsync().AsTask()));
        }
    }
}
```

- [ ] **Step 2: Run, verify it can fail today (flaky) or passes; then make it deterministic.**

Run: `dotnet test /home/dbechstein/Projekte/voip/.claude/worktrees/feat+interop-soak-audit/tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter RtpMediaLoopbackParallelStartTests`
Note: without the retry this may pass or intermittently throw `SocketException: AddressAlreadyInUse`. The retry below makes it deterministic. (A flaky test is not a proof — the retry is the point.)

- [ ] **Step 3: Add bounded retry to `StartAsync`.** Replace the current `StartAsync` in `RtpMediaLoopback.cs` with (keeps the exception-safe cleanup from Phase 0.4, adds retry on bind collision):

```csharp
    /// <summary>
    /// Bindet beide Legs auf freie Loopback-Ports und startet ihre Medienpfade. Wiederholt bei
    /// Port-Bind-Kollision (<see cref="System.Net.Sockets.SocketError.AddressAlreadyInUse"/>) mit
    /// frischen Ports bis zu <paramref name="maxAttempts"/> mal — das schließt das TOCTOU-Fenster
    /// von <c>FreeUdpPort</c> unter Parallelität.
    /// </summary>
    public static async Task<RtpMediaLoopback> StartAsync(int maxAttempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryStartOnceAsync();
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < maxAttempts)
            {
                // Port zwischen Probe und Bind belegt — mit frischen Ports erneut versuchen.
            }
        }
    }

    private static async Task<RtpMediaLoopback> TryStartOnceAsync()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        var a = new RtpCallMediaSession(Parameters(portA, portB), NullLoggerFactory.Instance);
        try
        {
            var b = new RtpCallMediaSession(Parameters(portB, portA), NullLoggerFactory.Instance);
            try
            {
                await b.StartAsync();
                await a.StartAsync();
                return new RtpMediaLoopback(a, b);
            }
            catch
            {
                await b.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await a.DisposeAsync();
            throw;
        }
    }
```

Note: `SocketException`/`SocketError` are in `System.Net.Sockets`, already imported in this file. Keep the existing `Parameters`, `FreeUdpPort`, `RoundTripAsync`, `DisposeAsync`, ctor unchanged.

- [ ] **Step 4: Run parallel-start test, verify PASS** (deterministic now):

Run: `... --filter RtpMediaLoopbackParallelStartTests`
Expected: PASS. If it still throws AddressAlreadyInUse, the collision exceeded 5 attempts — report rather than raising the count blindly.

- [ ] **Step 5: Commit:**
```bash
git add tests/CalloraVoipSdk.InteropHarness/Media/RtpMediaLoopback.cs tests/CalloraVoipSdk.SoakTests/Media/RtpMediaLoopbackParallelStartTests.cs
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 1.1 — TOCTOU-robuste RtpMediaLoopback.StartAsync (bounded Retry)

Schließt den FreeUdpPort-TOCTOU-Nit aus dem Phase-0-Review: Port-Bind-Kollision
unter Parallelität wird mit frischen Ports wiederholt.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 2: Concurrency-Soak (Wellen paralleler Round-Trips)

**Files:**
- Test: `tests/CalloraVoipSdk.SoakTests/Soak/ConcurrentLoopbackSoakTests.cs`

- [ ] **Step 1: Failing soak test** — `tests/CalloraVoipSdk.SoakTests/Soak/ConcurrentLoopbackSoakTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class ConcurrentLoopbackSoakTests
{
    private const int Waves = 20;
    private const int Parallelism = 25;

    [Fact]
    public async Task ParallelLoopbackWaves_AllRoundTripsSucceed_AndMemoryStaysStable()
    {
        var sampler = new ResourceSampler();
        var payload = new byte[160];
        var samples = new List<ResourceSample>();
        var failures = 0;

        for (var wave = 0; wave < Waves; wave++)
        {
            var loopbacks = await Task.WhenAll(
                Enumerable.Range(0, Parallelism).Select(_ => RtpMediaLoopback.StartAsync()));

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

                failures += results.Count(ok => !ok);
            }
            finally
            {
                await Task.WhenAll(loopbacks.Select(l => l.DisposeAsync().AsTask()));
            }

            samples.Add(sampler.Capture());
        }

        Assert.Equal(0, failures);

        var trend = TrendAssertions.NoUpwardDrift(
            samples, s => s.ManagedBytes, toleranceRatio: 0.30);
        Assert.False(trend.HasDrift, trend.Detail);
    }
}
```

- [ ] **Step 2: Run, verify FAIL first** (only if the file is new and refers to nothing missing — it should compile; the "fail" here is really the first green run establishing the soak. If it compiles and passes on first run, that is acceptable for a soak test — note it in the report). 

Run: `... --filter ConcurrentLoopbackSoakTests`

- [ ] **Step 3: Run soak, verify PASS.** ~20 waves × 25 parallel round-trips. Expected: 0 failures, no memory drift at 30% tolerance.

**CRITICAL — if failures > 0 or DRIFT:** Do NOT weaken tolerance / lower parallelism / catch-and-ignore to force green. A real failure or drift under concurrency is a genuine finding. Report DONE_WITH_CONCERNS with the numbers (`failures`, `trend.Detail`); we record it in `docs/audit/INTEROP_SOAK_AUDIT.md` as a `Soak-Leak` or `Media-Defekt` and decide. Only a genuinely green result is committed.

- [ ] **Step 4: Commit (only if green):**
```bash
git add tests/CalloraVoipSdk.SoakTests/Soak/ConcurrentLoopbackSoakTests.cs
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 1.2 — Concurrency-Soak (Wellen paralleler Loopback-Round-Trips)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Phase-1-Abschluss

- [ ] Voller Build `dotnet build CalloraVoipSdk.sln -c Debug` → 0/0.
- [ ] `dotnet test tests/CalloraVoipSdk.SoakTests/...` (net9+net10) → alle grün.

**Damit steht:** der Concurrency-Soak-Fokus + der geschlossene TOCTOU-Nit. Nächste Phase: **Phase 2** — Media-Qualitäts-Drift (public `MediaQualitySnapshot`-Seam über `CallMediaRuntimeMetrics`, kontinuierlicher Call, Jitter/Loss-Trend) — eigener Plan mit eigener Ctor-Recherche.

## Spec-Abdeckung (Self-Review)

- Soak-Fokus „Last/Concurrency" (Spec §8) → Task 2. · Phase-0-Backlog-Nit `FreeUdpPort`-TOCTOU → Task 1.
- Kein autonomes Fixen (Spec §3) → Task 2 Step 3 (Fehler/Drift → Register, kein Test-Fudging).
- **Nicht in Phase 1:** Media-Qualitäts-Drift, Langzeit-Signaling (L3), Interop-Ebenen. Bewusst spätere Phasen.
