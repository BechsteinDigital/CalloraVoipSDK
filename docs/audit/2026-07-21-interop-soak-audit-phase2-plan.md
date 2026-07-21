# Phase 2 — Media-Qualitäts-Drift-Soak: Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** Den Soak-Fokus „Media-Qualitäts-Drift" abdecken — ein langer, kontinuierlicher Call, dessen Empfangsqualität (Jitter/Loss/RTT) über die Zeit gesammelt wird und nicht nach oben driftet.

**Architecture:** Neuer public `MediaQualitySnapshot`-Seam (kapselt das interne `CallMediaRuntimeMetrics`), eine generische `TrendAssertions.NoUpwardDrift<T>(…, Func<T,double>, …)`-Overload (Jitter ist `double`), und eine `RtpMediaLoopback.RunAndCollectQualityAsync(...)`-Methode (kontinuierlicher Send + Metrik-Sammlung über den erweiterten Ctor mit kurzem Publish-Intervall).

**Tech Stack:** .NET 8/9/10, xUnit, bestehende Harness-Primitiven.

---

## Verifizierte Fakten (Ground-Truth, gelesen)

- `internal readonly record struct CallMediaRuntimeMetrics(...)` — `src/Core/Application/Media/CallMediaRuntimeMetrics.cs`; Felder u. a. `CapturedAtUtc`, `PacketsDelivered`, `PacketsDroppedLate`, `PacketsDroppedOverflow`, `PacketsUnrecoverableLoss`, `EstimatedJitterMs` (double), `EstimatedRoundTripTimeMs` (double).
- `ICallMediaSession.RuntimeMetricsUpdated` — `event Action<CallMediaRuntimeMetrics>?`; feuert in `PublishRuntimeMetricsIfDue` im **Empfangspfad** (nach `DrainReadyPackets`) sobald `_metricsPublishInterval` abgelaufen ist. Der EMPFANGENDE Leg publiziert.
- `private static readonly TimeSpan DefaultMetricsPublishInterval = TimeSpan.FromSeconds(1)` (`RtpCallMediaSession.cs:26`).
- **Erweiterter internal Ctor** (`RtpCallMediaSession.cs:118`): `RtpCallMediaSession(CallMediaParameters, ILoggerFactory, JitterBufferOptions? jitterBufferOptions, TimeSpan? playoutInterval, TimeSpan? metricsPublishInterval, PayloadCodecKind? bridgeTapCodec = null, IDtlsSrtpHandshaker? = null, DtlsCertificate? = null)`. Mit `jitterBufferOptions: null, playoutInterval: null, metricsPublishInterval: <kurz>` verhaltensgleich zum 2-arg-Ctor außer schnellerem Metrik-Publish (der 2-arg-Ctor delegiert selbst mit `metricsPublishInterval: null`).
- `MediaQualitySnapshot.From(CallMediaRuntimeMetrics)` muss **internal** sein (Parametertyp ist internal); Harness hat `InternalsVisibleTo`. Der public Record selbst exponiert nur primitive Felder.
- Bestehende `TrendAssertions.NoUpwardDrift(IReadOnlyList<ResourceSample>, Func<ResourceSample,long>, …)` wird von 3 Tests genutzt (TrendAssertionsTests, RtpMediaLeakSoakTests, ConcurrentLoopbackSoakTests) — **muss unverändert grün bleiben**.

---

## File Structure

```text
tests/CalloraVoipSdk.InteropHarness/Metrics/MediaQualitySnapshot.cs     NEW: public Snapshot + internal From(CallMediaRuntimeMetrics)
tests/CalloraVoipSdk.InteropHarness/Metrics/TrendAssertions.cs          MODIFY: + generische NoUpwardDrift<T>(Func<T,double>)-Overload (long-Version unberührt)
tests/CalloraVoipSdk.InteropHarness/Media/RtpMediaLoopback.cs           MODIFY: StartAsync(+metricsPublishInterval), CreateSession-Helper, RunAndCollectQualityAsync
tests/CalloraVoipSdk.SoakTests/Metrics/TrendAssertionsGenericTests.cs   NEW
tests/CalloraVoipSdk.SoakTests/Media/RtpMediaQualityCollectionTests.cs  NEW
tests/CalloraVoipSdk.SoakTests/Soak/MediaQualityDriftSoakTests.cs       NEW
```

---

## Task 1: `MediaQualitySnapshot` + generische Trend-Overload

**Files:**
- Create: `tests/CalloraVoipSdk.InteropHarness/Metrics/MediaQualitySnapshot.cs`
- Modify: `tests/CalloraVoipSdk.InteropHarness/Metrics/TrendAssertions.cs`
- Test: `tests/CalloraVoipSdk.SoakTests/Metrics/TrendAssertionsGenericTests.cs`

- [ ] **Step 1: Failing test** — `tests/CalloraVoipSdk.SoakTests/Metrics/TrendAssertionsGenericTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Metrics;

public sealed class TrendAssertionsGenericTests
{
    private static MediaQualitySnapshot Jitter(double ms) => new(
        CapturedAtUtc: default, JitterMs: ms, RoundTripTimeMs: 0,
        PacketsDelivered: 0, PacketsDroppedLate: 0, PacketsDroppedOverflow: 0, PacketsUnrecoverableLoss: 0);

    [Fact]
    public void NoUpwardDrift_FlatJitter_HasNoDrift()
    {
        var s = new[] { Jitter(5.0), Jitter(5.2), Jitter(4.8), Jitter(5.1), Jitter(5.0) };
        var r = TrendAssertions.NoUpwardDrift(s, x => x.JitterMs, toleranceRatio: 0.20, metricName: "JitterMs");
        Assert.False(r.HasDrift);
    }

    [Fact]
    public void NoUpwardDrift_RisingJitter_DetectsDrift()
    {
        var s = new[] { Jitter(5.0), Jitter(10.0), Jitter(20.0), Jitter(35.0), Jitter(60.0) };
        var r = TrendAssertions.NoUpwardDrift(s, x => x.JitterMs, toleranceRatio: 0.20, metricName: "JitterMs");
        Assert.True(r.HasDrift);
        Assert.Contains("JitterMs", r.Detail, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test /home/dbechstein/Projekte/voip/.claude/worktrees/feat+interop-soak-audit/tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter TrendAssertionsGenericTests`. Expected: types/overload missing.

- [ ] **Step 3: MediaQualitySnapshot** — `tests/CalloraVoipSdk.InteropHarness/Metrics/MediaQualitySnapshot.cs`:

```csharp
using CalloraVoipSdk.Core.Application.Media;

namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Öffentliche Momentaufnahme der Empfangsqualität eines Media-Legs (Interop-/Soak-Auswertung).</summary>
/// <param name="CapturedAtUtc">Erfassungszeitpunkt (UTC).</param>
/// <param name="JitterMs">Geschätzter Inter-Arrival-Jitter in Millisekunden.</param>
/// <param name="RoundTripTimeMs">Geglättete RTT-Schätzung in Millisekunden.</param>
/// <param name="PacketsDelivered">Kumulativ an den Consumer ausgelieferte RTP-Pakete.</param>
/// <param name="PacketsDroppedLate">Kumulativ wegen überschrittener Playout-Deadline verworfen.</param>
/// <param name="PacketsDroppedOverflow">Kumulativ wegen erschöpfter Jitter-Buffer-Kapazität verworfen.</param>
/// <param name="PacketsUnrecoverableLoss">Kumulativ nicht verdeckbarer Verlust.</param>
public readonly record struct MediaQualitySnapshot(
    DateTimeOffset CapturedAtUtc,
    double JitterMs,
    double RoundTripTimeMs,
    long PacketsDelivered,
    long PacketsDroppedLate,
    long PacketsDroppedOverflow,
    long PacketsUnrecoverableLoss)
{
    /// <summary>Kapselt das interne Laufzeit-Metrik-Snapshot in einen öffentlichen Wert.</summary>
    internal static MediaQualitySnapshot From(CallMediaRuntimeMetrics m) => new(
        m.CapturedAtUtc, m.EstimatedJitterMs, m.EstimatedRoundTripTimeMs,
        m.PacketsDelivered, m.PacketsDroppedLate, m.PacketsDroppedOverflow, m.PacketsUnrecoverableLoss);
}
```

- [ ] **Step 4: Add generic overload to `TrendAssertions.cs`** (leave the existing `long`-overload and private `Median` untouched; add these two members):

```csharp
    /// <summary>
    /// Wie die <c>long</c>-Variante, aber für <see cref="double"/>-Metriken (z. B. Jitter). Nutzt einen
    /// relativen Floor (<paramref name="toleranceRatio"/> vom Startsockel) mit absolutem Mindest-Floor,
    /// damit ein Startsockel nahe 0 keine Fehlalarme erzeugt.
    /// </summary>
    /// <param name="samples">Chronologische Messreihe (mindestens 2 Werte).</param>
    /// <param name="selector">Extrahiert die zu prüfende Metrik.</param>
    /// <param name="toleranceRatio">Erlaubtes relatives Wachstum (z. B. 0.20 = 20 %).</param>
    /// <param name="metricName">Anzeigename der Metrik für die Begründung.</param>
    public static TrendResult NoUpwardDrift<T>(
        IReadOnlyList<T> samples,
        Func<T, double> selector,
        double toleranceRatio = 0.10,
        string metricName = "value")
    {
        if (samples.Count < 2)
            return new TrendResult(false, $"{metricName}: zu wenige Samples ({samples.Count}).");

        var bucket = Math.Max(1, samples.Count / 5);
        var start = MedianOfDouble(samples.Take(bucket).Select(selector));
        var end = MedianOfDouble(samples.Skip(samples.Count - bucket).Select(selector));

        var tolerance = Math.Max(1e-6, Math.Abs(start) * toleranceRatio);
        var threshold = start + tolerance;
        var hasDrift = end > threshold;
        var detail =
            $"{metricName}: Start≈{start:F3}, Ende≈{end:F3}, Schwelle={threshold:F3} " +
            $"(+{toleranceRatio:P0}) → {(hasDrift ? "DRIFT" : "stabil")}.";
        return new TrendResult(hasDrift, detail);
    }

    private static double MedianOfDouble(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0) return 0d;
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[mid] : (ordered[mid - 1] + ordered[mid]) / 2d;
    }
```

- [ ] **Step 5: Run generic test → PASS, and the existing `TrendAssertionsTests` → still PASS** (long-overload untouched):

Run: `... --filter "TrendAssertionsGenericTests|TrendAssertionsTests"`
Expected: all green (2 new + 3 existing).

- [ ] **Step 6: Commit:**
```bash
git add tests/CalloraVoipSdk.InteropHarness/Metrics/MediaQualitySnapshot.cs tests/CalloraVoipSdk.InteropHarness/Metrics/TrendAssertions.cs tests/CalloraVoipSdk.SoakTests/Metrics/TrendAssertionsGenericTests.cs
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 2.1 — MediaQualitySnapshot-Seam + generische Trend-Overload (double)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 2: `RtpMediaLoopback.RunAndCollectQualityAsync` (kontinuierlicher Call + Metrik-Sammlung)

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropHarness/Media/RtpMediaLoopback.cs`
- Test: `tests/CalloraVoipSdk.SoakTests/Media/RtpMediaQualityCollectionTests.cs`

- [ ] **Step 1: Failing test** — `tests/CalloraVoipSdk.SoakTests/Media/RtpMediaQualityCollectionTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Media;

namespace CalloraVoipSdk.SoakTests.Media;

public sealed class RtpMediaQualityCollectionTests
{
    [Fact]
    public async Task RunAndCollectQualityAsync_ShortCall_CollectsSnapshotsWithDeliveredPackets()
    {
        // Kurzes Metrik-Intervall → mehrere Snapshots in wenigen Sekunden.
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: TimeSpan.FromSeconds(3), frameInterval: TimeSpan.FromMilliseconds(20));

        Assert.NotEmpty(snapshots);
        Assert.True(snapshots[^1].PacketsDelivered > 0, "Es müssen Pakete ausgeliefert worden sein.");
    }
}
```

- [ ] **Step 2: Run, verify FAIL** — `... --filter RtpMediaQualityCollectionTests`. Expected: `StartAsync(metricsPublishInterval:)` / `RunAndCollectQualityAsync` missing.

- [ ] **Step 3: Modify `RtpMediaLoopback.cs`.** (a) Add `metricsPublishInterval` to `StartAsync`/`TryStartOnceAsync` and route both sessions through a `CreateSession` helper using the extended ctor. (b) Add `RunAndCollectQualityAsync`. Add `using System.Collections.Generic;` if needed (`CallMediaRuntimeMetrics` is already accessible via `InternalsVisibleTo`; import `CalloraVoipSdk.Core.Application.Media`).

Replace the current `StartAsync` + `TryStartOnceAsync` with:

```csharp
    /// <summary>
    /// Bindet beide Legs auf freie Loopback-Ports und startet ihre Medienpfade. Wiederholt bei
    /// Port-Bind-Kollision (<see cref="System.Net.Sockets.SocketError.AddressAlreadyInUse"/>) mit
    /// frischen Ports bis zu <paramref name="maxAttempts"/> mal. <paramref name="metricsPublishInterval"/>
    /// steuert das Laufzeit-Metrik-Publish-Intervall beider Legs (<see langword="null"/> = SDK-Default 1 s).
    /// </summary>
    public static async Task<RtpMediaLoopback> StartAsync(
        int maxAttempts = 5, TimeSpan? metricsPublishInterval = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryStartOnceAsync(metricsPublishInterval);
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < maxAttempts)
            {
                // Port zwischen Probe und Bind belegt — mit frischen Ports erneut versuchen.
            }
        }
    }

    private static async Task<RtpMediaLoopback> TryStartOnceAsync(TimeSpan? metricsPublishInterval)
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        var a = CreateSession(portA, portB, metricsPublishInterval);
        try
        {
            var b = CreateSession(portB, portA, metricsPublishInterval);
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

    private static RtpCallMediaSession CreateSession(
        int localPort, int remotePort, TimeSpan? metricsPublishInterval) =>
        new(Parameters(localPort, remotePort), NullLoggerFactory.Instance,
            jitterBufferOptions: null, playoutInterval: null, metricsPublishInterval: metricsPublishInterval);
```

Then add this method (after `RoundTripAsync`):

```csharp
    /// <summary>
    /// Sendet <paramref name="duration"/> lang kontinuierlich Frames von Leg A (alle
    /// <paramref name="frameInterval"/>) und sammelt die bei Leg B gemeldeten Empfangs-Qualitäts-
    /// Snapshots. Für Qualitäts-Drift-Soaks: ein langer Call statt vieler kurzer.
    /// </summary>
    public async Task<IReadOnlyList<MediaQualitySnapshot>> RunAndCollectQualityAsync(
        TimeSpan duration, TimeSpan frameInterval)
    {
        var snapshots = new List<MediaQualitySnapshot>();
        var gate = new object();
        void OnMetrics(CallMediaRuntimeMetrics m)
        {
            lock (gate) snapshots.Add(MediaQualitySnapshot.From(m));
        }

        _b.RuntimeMetricsUpdated += OnMetrics;
        try
        {
            using var cts = new CancellationTokenSource(duration);
            var frame = new CallAudioFrame(new byte[160], PcmuPayloadType, (uint)SamplesPerPacket);
            try
            {
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    await _a.SendFrameAsync(frame, cts.Token);
                    await Task.Delay(frameInterval, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Erwartetes Ende der Lauf-Dauer.
            }

            lock (gate) return snapshots.ToArray();
        }
        finally
        {
            _b.RuntimeMetricsUpdated -= OnMetrics;
        }
    }
```

Add the required usings at the top of the file if not present: `using System.Collections.Generic;`, `using CalloraVoipSdk.Core.Application.Media;`, `using CalloraVoipSdk.InteropHarness.Metrics;`.

- [ ] **Step 4: Run quality-collection test → PASS.** Also run the full SoakTests project to confirm the `StartAsync` change didn't regress existing tests (round-trip, leak soak, parallel-start, concurrency): `... -f net10.0`.

- [ ] **Step 5: Commit:**
```bash
git add tests/CalloraVoipSdk.InteropHarness/Media/RtpMediaLoopback.cs tests/CalloraVoipSdk.SoakTests/Media/RtpMediaQualityCollectionTests.cs
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 2.2 — RtpMediaLoopback.RunAndCollectQualityAsync (kontinuierlicher Call + Metrik-Sammlung)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 3: Media-Qualitäts-Drift-Soak

**Files:**
- Test: `tests/CalloraVoipSdk.SoakTests/Soak/MediaQualityDriftSoakTests.cs`

- [ ] **Step 1: Soak test** — `tests/CalloraVoipSdk.SoakTests/Soak/MediaQualityDriftSoakTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class MediaQualityDriftSoakTests
{
    [Fact]
    public async Task LongCall_JitterDoesNotDrift_AndLossStaysLow()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: TimeSpan.FromSeconds(20), frameInterval: TimeSpan.FromMilliseconds(20));

        // Genug Snapshots für einen belastbaren Trend (200 ms Intervall über 20 s → ~100).
        Assert.True(snapshots.Count >= 10, $"Zu wenige Snapshots: {snapshots.Count}");

        // Jitter darf über den Call nicht nach oben driften.
        var jitter = TrendAssertions.NoUpwardDrift(
            snapshots, s => s.JitterMs, toleranceRatio: 0.50, metricName: "JitterMs");
        Assert.False(jitter.HasDrift, jitter.Detail);

        // Auf Loopback (kein echter Verlust) bleibt unrecoverable loss bei 0; späte/overflow-Drops minimal.
        var last = snapshots[^1];
        Assert.Equal(0, last.PacketsUnrecoverableLoss);
        Assert.True(last.PacketsDelivered > 0, "Es müssen Pakete ausgeliefert worden sein.");
    }
}
```

- [ ] **Step 2: Run.** ~20 s call. Expected green: jitter stable, loss 0.

- [ ] **Step 3 — CRITICAL if DRIFT or non-zero unrecoverable loss:** Do NOT relax the tolerance or the loss assertion to force green. On pure UDP loopback there is no real loss, so a positive `PacketsUnrecoverableLoss` or genuine jitter drift is a real finding in our own jitter-buffer/receive path. Report **DONE_WITH_CONCERNS** with `jitter.Detail` and the loss/late/overflow counts; we record it in `docs/audit/INTEROP_SOAK_AUDIT.md` as `Media-Defekt`. Only commit if genuinely green.

- [ ] **Step 4: Commit (only if green):**
```bash
git add tests/CalloraVoipSdk.SoakTests/Soak/MediaQualityDriftSoakTests.cs
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 2.3 — Media-Qualitäts-Drift-Soak (langer Call, Jitter-Trend + Loss)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Phase-2-Abschluss

- [ ] Voller Build `dotnet build CalloraVoipSdk.sln -c Debug` → 0/0.
- [ ] `dotnet test tests/CalloraVoipSdk.SoakTests/...` (net9+net10) → alle grün.
- [ ] `origin/main`-Check + ggf. mergen.

**Damit steht:** der Media-Qualitäts-Drift-Fokus. Verbleibend: Langzeit-Signaling (L3) → dann echte Interop (Asterisk L4).

## Spec-Abdeckung (Self-Review)

- Soak-Fokus „Media-Qualitäts-Drift" (Spec §8) → Task 3. · Public-Seam über internal Metrics (Facade-Coupling-konform) → Task 1/2.
- Bestehende long-Trend-Tests unberührt (verhaltensbewahrend) → Task 1 Step 5. · Kein Test-Fudging bei Drift/Loss → Task 3 Step 3.
- **Nicht in Phase 2:** Langzeit-Signaling (L3), Interop-Ebenen. Spätere Phasen.
