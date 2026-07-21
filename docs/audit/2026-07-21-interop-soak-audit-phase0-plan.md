# Phase 0 — Fundament: Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Das gemeinsame Test-Fundament bauen — Harness-Projekt, Ressourcen-Sampler mit Trend-Assert, Audit-Register-Gerüst und eine erste L2-Media-Loopback-Fixture als vertikaler Durchstich (zwei `RtpCallMediaSession` über echten UDP-Loopback, Audio-Round-Trip).

**Architecture:** Neue Klassenbibliothek `CalloraVoipSdk.InteropHarness` kapselt den (internal) Zugriff auf die Non-WebRTC-Komponenten und bietet den Test-Projekten (`SoakTests`, später `InteropTests`) eine **public** API. Da `RtpCallMediaSession`/`CallAudioFrame` `internal` sind, wird das Harness-Assembly in `InternalsVisibleTo` eingetragen — das ist der erste dokumentierte `Facade-Coupling-Gap`.

**Tech Stack:** .NET 8/9/10 (multi-target), xUnit 2.4.2, `RtpCallMediaSession` (Core.Infrastructure.Rtp), `CallMediaParameters`/`CallAudioFrame` (Core.Domain.Calls), `NullLoggerFactory`.

---

## Verifizierte API-Fakten (Ground-Truth, gelesen)

- `internal sealed class RtpCallMediaSession : ICallMediaSession` — `src/Core/Infrastructure/Rtp/RtpCallMediaSession.cs:22`; Ctor `internal RtpCallMediaSession(CallMediaParameters, ILoggerFactory)` (2-arg, plain RTP) `:106`.
- `ICallMediaSession` — `src/Core/Application/Media/ICallMediaSession.cs`:
  - `Task StartAsync(CancellationToken ct = default)`
  - `Task SendFrameAsync(CallAudioFrame frame, CancellationToken ct = default)`
  - `event Action<CallAudioFrame>? FrameReceived`
  - `ValueTask DisposeAsync()` (→ `await using`)
- `internal readonly record struct CallAudioFrame(byte[] Payload, int PayloadType, uint DurationRtpUnits)` — `src/Core/Domain/Calls/CallAudioFrame.cs:3`.
- `sealed record CallMediaParameters` — `src/Core/Domain/Calls/CallMediaParameters.cs:15`; required: `LocalEndPoint`, `RemoteEndPoint`, `PayloadType`, `ClockRate`, `SamplesPerPacket`. Plain RTP = Defaults (`IsDtlsNegotiated=false`, `IsSrtpNegotiated=false`).
- Plain-RTP-Verdrahtungsmuster belegt in `DtlsMediaPathE2eTests.cs` (mit DTLS) und `RtpCallMediaSessionIceInboundTests.cs:89` / `VideoMediaStreamE2eTests.cs:134` (2-arg-Ctor, plain).
- `InternalsVisibleTo` in `src/Core/Properties/AssemblyInfo.cs:3-11` — **`CalloraVoipSdk.InteropHarness` fehlt dort.**
- `SoakTests.csproj`: net8/9/10, xUnit 2.4.2, `Microsoft.NET.Test.Sdk` 17.6.0; **nicht in `CalloraVoipSdk.sln`.**

---

## File Structure

```text
tests/CalloraVoipSdk.InteropHarness/
  CalloraVoipSdk.InteropHarness.csproj      Klassenbib, Core-Ref, Internals-Konsument
  Metrics/ResourceSample.cs                 Momentaufnahme (Speicher/Threads/Handles)
  Metrics/ResourceSampler.cs                Capture() → ResourceSample
  Metrics/TrendAssertions.cs                NoUpwardDrift() → TrendResult
  Metrics/TrendResult.cs                    Ergebnis-Record
  Audit/Finding.cs                          Finding-Record (Typ, Evidenz, Fundstelle …)
  Audit/AuditFindingFormatter.cs            Finding → Markdown-Tabellenzeile
  Media/RtpMediaLoopback.cs                 L2-Fixture: 2× RtpCallMediaSession, public RoundTripAsync

tests/CalloraVoipSdk.SoakTests/
  Metrics/TrendAssertionsTests.cs
  Audit/AuditFindingFormatterTests.cs
  Media/RtpMediaLoopbackRoundTripTests.cs
  Soak/RtpMediaLeakSoakTests.cs

docs/audit/INTEROP_SOAK_AUDIT.md            lebendes Register (Gerüst)

src/Core/Properties/AssemblyInfo.cs         +1 InternalsVisibleTo
CalloraVoipSdk.sln                          +InteropHarness +SoakTests
```

---

## Task 1: Harness-Projekt + Solution-Integration + Internals-Zugriff

**Files:**
- Create: `tests/CalloraVoipSdk.InteropHarness/CalloraVoipSdk.InteropHarness.csproj`
- Create: `tests/CalloraVoipSdk.InteropHarness/HarnessMarker.cs`
- Modify: `src/Core/Properties/AssemblyInfo.cs` (nach Zeile 11)
- Modify: `CalloraVoipSdk.sln` (via `dotnet sln add`)

- [ ] **Step 1: Harness-csproj anlegen**

`tests/CalloraVoipSdk.InteropHarness/CalloraVoipSdk.InteropHarness.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <AssemblyName>CalloraVoipSdk.InteropHarness</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Core\CalloraVoipSdk.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Platzhalter-Marker anlegen** (damit die Assembly kompiliert, bevor echte Typen dazukommen)

`tests/CalloraVoipSdk.InteropHarness/HarnessMarker.cs`:

```csharp
namespace CalloraVoipSdk.InteropHarness;

/// <summary>Assembly-Marker für das Interop-/Soak-Test-Harness.</summary>
public static class HarnessMarker
{
    /// <summary>Stabiler Name des Harness zur Selbstidentifikation.</summary>
    public const string Name = "CalloraVoipSdk.InteropHarness";
}
```

- [ ] **Step 3: Internals-Zugriff freischalten**

In `src/Core/Properties/AssemblyInfo.cs` nach der letzten `InternalsVisibleTo`-Zeile (`CalloraVoipSdk.Audio.Windows`) ergänzen:

```csharp
[assembly: InternalsVisibleTo("CalloraVoipSdk.InteropHarness")]
```

- [ ] **Step 4: Beide Projekte in die Solution aufnehmen**

Run:
```bash
dotnet sln CalloraVoipSdk.sln add tests/CalloraVoipSdk.InteropHarness/CalloraVoipSdk.InteropHarness.csproj
dotnet sln CalloraVoipSdk.sln add tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj
```
Expected: „Das Projekt … wurde der Projektmappe hinzugefügt." (2×)

- [ ] **Step 5: SoakTests referenziert das Harness**

Run:
```bash
dotnet add tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj reference tests/CalloraVoipSdk.InteropHarness/CalloraVoipSdk.InteropHarness.csproj
```

- [ ] **Step 6: Build verifizieren**

Run: `dotnet build tests/CalloraVoipSdk.InteropHarness/CalloraVoipSdk.InteropHarness.csproj -f net10.0`
Expected: `Der Buildvorgang wurde erfolgreich ausgeführt. 0 Warnung(en) 0 Fehler`

- [ ] **Step 7: Commit**

```bash
git add tests/CalloraVoipSdk.InteropHarness/ src/Core/Properties/AssemblyInfo.cs CalloraVoipSdk.sln tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj
git commit -m "feat(interop-soak): Phase 0.1 — InteropHarness-Projekt + Solution + Internals-Zugriff"
```

---

## Task 2: Ressourcen-Sampler + Trend-Assert

Deterministisch, SDK-unabhängig. Der Trend-Assert erkennt monotone Aufwärts-Drift (Leak-Signatur) vs. stabilen Sockel.

**Files:**
- Create: `tests/CalloraVoipSdk.InteropHarness/Metrics/ResourceSample.cs`
- Create: `tests/CalloraVoipSdk.InteropHarness/Metrics/ResourceSampler.cs`
- Create: `tests/CalloraVoipSdk.InteropHarness/Metrics/TrendResult.cs`
- Create: `tests/CalloraVoipSdk.InteropHarness/Metrics/TrendAssertions.cs`
- Test: `tests/CalloraVoipSdk.SoakTests/Metrics/TrendAssertionsTests.cs`

- [ ] **Step 1: Failing test schreiben**

`tests/CalloraVoipSdk.SoakTests/Metrics/TrendAssertionsTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Metrics;

public sealed class TrendAssertionsTests
{
    private static ResourceSample At(long managedBytes) => new(
        SampleIndex: 0, ManagedBytes: managedBytes, ThreadCount: 10, HandleCount: 100);

    [Fact]
    public void NoUpwardDrift_FlatSeries_HasNoDrift()
    {
        var samples = new[] { At(1000), At(1010), At(995), At(1005), At(1000) };

        var result = TrendAssertions.NoUpwardDrift(
            samples, s => s.ManagedBytes, toleranceRatio: 0.10);

        Assert.False(result.HasDrift);
    }

    [Fact]
    public void NoUpwardDrift_MonotonicGrowth_DetectsDrift()
    {
        var samples = new[] { At(1000), At(2000), At(3000), At(4000), At(5000) };

        var result = TrendAssertions.NoUpwardDrift(
            samples, s => s.ManagedBytes, toleranceRatio: 0.10);

        Assert.True(result.HasDrift);
        Assert.Contains("ManagedBytes", result.Detail, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Test bauen, Fehlschlag verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter TrendAssertionsTests`
Expected: FAIL — `ResourceSample`/`TrendAssertions` existieren nicht (CS0246).

- [ ] **Step 3: ResourceSample implementieren**

`tests/CalloraVoipSdk.InteropHarness/Metrics/ResourceSample.cs`:

```csharp
namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Eine Momentaufnahme prozessweiter Ressourcenzähler zu einem Soak-Zeitpunkt.</summary>
/// <param name="SampleIndex">Fortlaufender Index innerhalb einer Soak-Serie.</param>
/// <param name="ManagedBytes">Verwalteter Heap in Bytes (<see cref="GC.GetTotalMemory(bool)"/>).</param>
/// <param name="ThreadCount">Prozess-Threadanzahl.</param>
/// <param name="HandleCount">Betriebssystem-Handle-Anzahl des Prozesses.</param>
public readonly record struct ResourceSample(
    int SampleIndex,
    long ManagedBytes,
    int ThreadCount,
    int HandleCount);
```

- [ ] **Step 4: TrendResult implementieren**

`tests/CalloraVoipSdk.InteropHarness/Metrics/TrendResult.cs`:

```csharp
namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Ergebnis einer Trend-Auswertung über eine Soak-Messreihe.</summary>
/// <param name="HasDrift">True, wenn die Reihe die tolerierte Aufwärts-Drift überschreitet.</param>
/// <param name="Detail">Menschlich lesbare Begründung (Metrik, Start-/Endwert, Schwelle).</param>
public readonly record struct TrendResult(bool HasDrift, string Detail);
```

- [ ] **Step 5: TrendAssertions implementieren**

`tests/CalloraVoipSdk.InteropHarness/Metrics/TrendAssertions.cs`:

```csharp
namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>
/// Trend-Auswertungen über Soak-Messreihen. Vergleicht einen robusten Anfangs- gegen einen
/// Endsockel (Median der ersten/letzten Fünftel), um einmalige Ausreißer zu ignorieren und
/// echte monotone Drift (Leak-Signatur) zu erkennen.
/// </summary>
public static class TrendAssertions
{
    /// <summary>
    /// Prüft, ob die per <paramref name="selector"/> ausgewählte Metrik über die Reihe
    /// stärker als <paramref name="toleranceRatio"/> (relativ zum Startsockel) aufwärts driftet.
    /// </summary>
    /// <param name="samples">Chronologische Messreihe (mindestens 2 Werte).</param>
    /// <param name="selector">Extrahiert die zu prüfende Metrik aus einem Sample.</param>
    /// <param name="metricName">Anzeigename der Metrik für die Begründung.</param>
    /// <param name="toleranceRatio">Erlaubtes relatives Wachstum (z. B. 0.10 = 10 %).</param>
    public static TrendResult NoUpwardDrift(
        IReadOnlyList<ResourceSample> samples,
        Func<ResourceSample, long> selector,
        double toleranceRatio = 0.10,
        string metricName = "ManagedBytes")
    {
        if (samples.Count < 2)
            return new TrendResult(false, $"{metricName}: zu wenige Samples ({samples.Count}).");

        var bucket = Math.Max(1, samples.Count / 5);
        var start = Median(samples.Take(bucket).Select(selector));
        var end = Median(samples.Skip(samples.Count - bucket).Select(selector));

        var threshold = start + (long)Math.Ceiling(Math.Abs(start) * toleranceRatio);
        var hasDrift = end > threshold;
        var detail =
            $"{metricName}: Start≈{start}, Ende≈{end}, Schwelle={threshold} " +
            $"(+{toleranceRatio:P0}) → {(hasDrift ? "DRIFT" : "stabil")}.";
        return new TrendResult(hasDrift, detail);
    }

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0) return 0;
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[mid]
            : (ordered[mid - 1] + ordered[mid]) / 2;
    }
}
```

- [ ] **Step 6: ResourceSampler implementieren**

`tests/CalloraVoipSdk.InteropHarness/Metrics/ResourceSampler.cs`:

```csharp
using System.Diagnostics;

namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Erfasst prozessweite Ressourcenzähler als <see cref="ResourceSample"/>.</summary>
public sealed class ResourceSampler
{
    private int _index;

    /// <summary>
    /// Nimmt eine Momentaufnahme. <paramref name="forceGc"/> erzwingt eine vollständige
    /// Collection vor der Speichermessung, damit nur nicht mehr erreichbarer Heap als Sockel zählt.
    /// </summary>
    public ResourceSample Capture(bool forceGc = true)
    {
        if (forceGc)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        using var process = Process.GetCurrentProcess();
        return new ResourceSample(
            SampleIndex: _index++,
            ManagedBytes: GC.GetTotalMemory(forceFullCollection: false),
            ThreadCount: process.Threads.Count,
            HandleCount: process.HandleCount);
    }
}
```

- [ ] **Step 7: Test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter TrendAssertionsTests`
Expected: PASS (2 Tests).

- [ ] **Step 8: Commit**

```bash
git add tests/CalloraVoipSdk.InteropHarness/Metrics/ tests/CalloraVoipSdk.SoakTests/Metrics/
git commit -m "feat(interop-soak): Phase 0.2 — Ressourcen-Sampler + Trend-Assert"
```

---

## Task 3: Audit-Register-Gerüst + Finding-Formatter

**Files:**
- Create: `docs/audit/INTEROP_SOAK_AUDIT.md`
- Create: `tests/CalloraVoipSdk.InteropHarness/Audit/Finding.cs`
- Create: `tests/CalloraVoipSdk.InteropHarness/Audit/AuditFindingFormatter.cs`
- Test: `tests/CalloraVoipSdk.SoakTests/Audit/AuditFindingFormatterTests.cs`

- [ ] **Step 1: Register-Gerüst anlegen**

`docs/audit/INTEROP_SOAK_AUDIT.md`:

```markdown
# Interop- & Soak-Audit — Fehlerregister

> Lebendes Register. **Nur Dokumentation — kein autonomes Fixen.** Jeder Fix ist ein separates,
> eigens freigegebenes Paket. Design: `docs/audit/2026-07-21-interop-soak-audit-design.md`.

**Finding-Typen:** `Interop-Abweichung` · `Soak-Leak` · `Media-Defekt` · `Wire-Robustheit` · `Facade-Coupling-Gap`

| FID | Typ | Evidenz (Test/Peer) | Symptom | Fehlerquelle | Fundstelle (Datei:Zeile) | Fix-Vorschlag | Schweregrad | Status |
|-----|-----|---------------------|---------|--------------|--------------------------|---------------|-------------|--------|
| F001 | Facade-Coupling-Gap | Phase 0.1 (InteropHarness-Setup) | L0–L3-Komponenten (`RtpCallMediaSession` u. a.) sind `internal`; Test unter der Facade erfordert `InternalsVisibleTo`-Eintrag | Bewusste Kapselung: Sub-Facade-Typen nicht öffentlich | `src/Core/Infrastructure/Rtp/RtpCallMediaSession.cs:22`, `src/Core/Properties/AssemblyInfo.cs:3` | Kein Fix — dokumentiert. Bewertung, ob ein schmales öffentliches Test-/Diagnose-Seam sinnvoll ist, in späterem Paket | Info | dokumentiert |
```

- [ ] **Step 2: Failing test schreiben**

`tests/CalloraVoipSdk.SoakTests/Audit/AuditFindingFormatterTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Audit;

namespace CalloraVoipSdk.SoakTests.Audit;

public sealed class AuditFindingFormatterTests
{
    [Fact]
    public void ToMarkdownRow_RendersAllFields_AsSinglePipeRow()
    {
        var finding = new Finding(
            Fid: "F002",
            Type: "Soak-Leak",
            Evidence: "RtpMediaLeakSoakTests",
            Symptom: "ManagedBytes driftet über 10.000 Round-Trips",
            RootCause: "unklar",
            Location: "src/Core/Infrastructure/Rtp/RtpCallMediaSession.cs:255",
            FixProposal: "n/a — dokumentiert",
            Severity: "offen",
            Status: "offen");

        var row = AuditFindingFormatter.ToMarkdownRow(finding);

        Assert.StartsWith("| F002 | Soak-Leak |", row);
        Assert.EndsWith("| offen |", row);
        Assert.DoesNotContain("\n", row); // eine einzige Tabellenzeile
    }

    [Fact]
    public void ToMarkdownRow_EscapesPipeCharacters()
    {
        var finding = new Finding(
            Fid: "F003", Type: "Wire-Robustheit", Evidence: "x",
            Symptom: "SDP a|b kaputt", RootCause: "x", Location: "x",
            FixProposal: "x", Severity: "x", Status: "x");

        var row = AuditFindingFormatter.ToMarkdownRow(finding);

        Assert.Contains(@"a\|b", row); // Pipe im Text escaped, bricht die Tabelle nicht
    }
}
```

- [ ] **Step 3: Test bauen, Fehlschlag verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter AuditFindingFormatterTests`
Expected: FAIL — `Finding`/`AuditFindingFormatter` existieren nicht.

- [ ] **Step 4: Finding implementieren**

`tests/CalloraVoipSdk.InteropHarness/Audit/Finding.cs`:

```csharp
namespace CalloraVoipSdk.InteropHarness.Audit;

/// <summary>Ein Eintrag im Interop-/Soak-Audit-Register (eine Tabellenzeile).</summary>
/// <param name="Fid">Fortlaufende Finding-ID (z. B. F002).</param>
/// <param name="Type">Finding-Typ (Interop-Abweichung, Soak-Leak, Media-Defekt, Wire-Robustheit, Facade-Coupling-Gap).</param>
/// <param name="Evidence">Reproduzierender Test bzw. Gegenstelle.</param>
/// <param name="Symptom">Beobachtetes Verhalten.</param>
/// <param name="RootCause">Fehlerquelle/Root-Cause-Kategorie.</param>
/// <param name="Location">Betroffene <c>Datei:Zeile</c> im SDK.</param>
/// <param name="FixProposal">Vorgeschlagener Fix — <b>nicht ausgeführt</b>.</param>
/// <param name="Severity">Schweregrad.</param>
/// <param name="Status">Bearbeitungsstatus.</param>
public readonly record struct Finding(
    string Fid,
    string Type,
    string Evidence,
    string Symptom,
    string RootCause,
    string Location,
    string FixProposal,
    string Severity,
    string Status);
```

- [ ] **Step 5: AuditFindingFormatter implementieren**

`tests/CalloraVoipSdk.InteropHarness/Audit/AuditFindingFormatter.cs`:

```csharp
namespace CalloraVoipSdk.InteropHarness.Audit;

/// <summary>Rendert <see cref="Finding"/>s als Markdown-Tabellenzeilen fürs Audit-Register.</summary>
public static class AuditFindingFormatter
{
    /// <summary>Formatiert ein Finding als eine einzelne Markdown-Tabellenzeile.</summary>
    public static string ToMarkdownRow(Finding f) =>
        "| " + string.Join(" | ", new[]
        {
            Cell(f.Fid), Cell(f.Type), Cell(f.Evidence), Cell(f.Symptom),
            Cell(f.RootCause), Cell(f.Location), Cell(f.FixProposal),
            Cell(f.Severity), Cell(f.Status),
        }) + " |";

    private static string Cell(string value) =>
        (value ?? string.Empty).Replace("|", @"\|").Replace("\r", " ").Replace("\n", " ");
}
```

- [ ] **Step 6: Test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter AuditFindingFormatterTests`
Expected: PASS (2 Tests).

- [ ] **Step 7: Commit**

```bash
git add tests/CalloraVoipSdk.InteropHarness/Audit/ tests/CalloraVoipSdk.SoakTests/Audit/ docs/audit/INTEROP_SOAK_AUDIT.md
git commit -m "feat(interop-soak): Phase 0.3 — Audit-Register-Gerüst + Finding-Formatter (F001 dokumentiert)"
```

---

## Task 4: L2-Media-Loopback-Fixture (Durchstich) + erster Leak-Soak

Zwei `RtpCallMediaSession` über UDP-Loopback, plain RTP (PCMU). Public `RoundTripAsync` versteckt den internal `CallAudioFrame`. Dann ein Leak-Soak über N Round-Trips gegen den Trend-Assert.

**Files:**
- Create: `tests/CalloraVoipSdk.InteropHarness/Media/RtpMediaLoopback.cs`
- Test: `tests/CalloraVoipSdk.SoakTests/Media/RtpMediaLoopbackRoundTripTests.cs`
- Test: `tests/CalloraVoipSdk.SoakTests/Soak/RtpMediaLeakSoakTests.cs`

- [ ] **Step 1: Failing round-trip test schreiben**

`tests/CalloraVoipSdk.SoakTests/Media/RtpMediaLoopbackRoundTripTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Media;

namespace CalloraVoipSdk.SoakTests.Media;

public sealed class RtpMediaLoopbackRoundTripTests
{
    [Fact]
    public async Task RoundTripAsync_PlainRtp_DeliversPayloadToPeer()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync();

        var payload = new byte[160];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;

        var received = await loopback.RoundTripAsync(payload, TimeSpan.FromSeconds(10));

        Assert.Equal(payload, received);
    }
}
```

- [ ] **Step 2: Test bauen, Fehlschlag verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter RtpMediaLoopbackRoundTripTests`
Expected: FAIL — `RtpMediaLoopback` existiert nicht.

- [ ] **Step 3: RtpMediaLoopback implementieren**

`tests/CalloraVoipSdk.InteropHarness/Media/RtpMediaLoopback.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.InteropHarness.Media;

/// <summary>
/// L2-Fixture: zwei <see cref="RtpCallMediaSession"/> über echten UDP-Loopback (plain RTP, PCMU).
/// Kapselt den internal <c>CallAudioFrame</c> und bietet den Test-Projekten eine public API.
/// </summary>
public sealed class RtpMediaLoopback : IAsyncDisposable
{
    private const int PcmuPayloadType = 0;
    private const int ClockRate = 8000;
    private const int SamplesPerPacket = 160;

    private readonly RtpCallMediaSession _a;
    private readonly RtpCallMediaSession _b;

    private RtpMediaLoopback(RtpCallMediaSession a, RtpCallMediaSession b)
    {
        _a = a;
        _b = b;
    }

    /// <summary>Bindet beide Legs auf Loopback und startet ihre Medienpfade.</summary>
    public static async Task<RtpMediaLoopback> StartAsync()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        var a = new RtpCallMediaSession(Parameters(portA, portB), NullLoggerFactory.Instance);
        var b = new RtpCallMediaSession(Parameters(portB, portA), NullLoggerFactory.Instance);

        await b.StartAsync();
        await a.StartAsync();
        return new RtpMediaLoopback(a, b);
    }

    /// <summary>
    /// Sendet <paramref name="payload"/> von Leg A und gibt das erste bei Leg B empfangene
    /// RTP-Payload zurück. Sendet wiederholt (20 ms) bis zum Empfang oder <paramref name="timeout"/>,
    /// um Playout-Anlauf zu überbrücken.
    /// </summary>
    public async Task<byte[]> RoundTripAsync(byte[] payload, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(CallAudioFrame f) => tcs.TrySetResult((byte[])f.Payload.Clone());
        _b.FrameReceived += OnFrame;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var frame = new CallAudioFrame(payload, PcmuPayloadType, (uint)SamplesPerPacket);
            while (!tcs.Task.IsCompleted)
            {
                cts.Token.ThrowIfCancellationRequested();
                await _a.SendFrameAsync(frame, cts.Token);
                await Task.Delay(20, cts.Token);
            }
            return await tcs.Task;
        }
        finally
        {
            _b.FrameReceived -= OnFrame;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static CallMediaParameters Parameters(int localPort, int remotePort) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        PayloadType = PcmuPayloadType,
        ClockRate = ClockRate,
        SamplesPerPacket = SamplesPerPacket,
    };

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
```

- [ ] **Step 4: Round-trip test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter RtpMediaLoopbackRoundTripTests`
Expected: PASS. (Falls FAIL wegen Playout-Timing: `timeout` in Step 1 auf 15 s erhöhen; die Sendeschleife ist bereits robust gegen Anlaufverzögerung.)

- [ ] **Step 5: Failing leak-soak test schreiben**

`tests/CalloraVoipSdk.SoakTests/Soak/RtpMediaLeakSoakTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class RtpMediaLeakSoakTests
{
    // CI-Kurzprofil: klein genug für die Pipeline, groß genug, dass ein echter
    // Per-Iteration-Leak den 10%-Trend-Schwellwert überschreitet.
    private const int Iterations = 500;

    [Fact]
    public async Task RepeatedLoopbackCalls_DoNotDriftManagedMemoryUpward()
    {
        var sampler = new ResourceSampler();
        var payload = new byte[160];
        var samples = new List<ResourceSample>();

        for (var i = 0; i < Iterations; i++)
        {
            await using (var loopback = await RtpMediaLoopback.StartAsync())
            {
                _ = await loopback.RoundTripAsync(payload, TimeSpan.FromSeconds(10));
            }

            if (i % 25 == 0)
                samples.Add(sampler.Capture());
        }
        samples.Add(sampler.Capture());

        var result = TrendAssertions.NoUpwardDrift(
            samples, s => s.ManagedBytes, toleranceRatio: 0.25);

        Assert.False(result.HasDrift, result.Detail);
    }
}
```

- [ ] **Step 6: Soak-Test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter RtpMediaLeakSoakTests`
Expected: PASS. Falls DRIFT: **kein Test-Fix** — der Befund wird als `Soak-Leak` ins Register (`docs/audit/INTEROP_SOAK_AUDIT.md`) eingetragen (`result.Detail` als Symptom, Fundstelle über einen Folge-Lauf mit dotnet-counters/dotMemory eingrenzen). Das ist ein echtes Ergebnis der Kampagne, kein Blocker.

- [ ] **Step 7: Commit**

```bash
git add tests/CalloraVoipSdk.InteropHarness/Media/ tests/CalloraVoipSdk.SoakTests/Media/ tests/CalloraVoipSdk.SoakTests/Soak/
git commit -m "feat(interop-soak): Phase 0.4 — L2 RtpMediaLoopback-Fixture + erster Leak-Soak (Durchstich)"
```

---

## Phase-0-Abschluss

- [ ] **Voller Build + Soak-Projekt-Test** über alle Frameworks:

Run: `dotnet build CalloraVoipSdk.sln -c Debug` → 0 Fehler.
Run: `dotnet test tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj` → alle grün (net8/9/10).

- [ ] **Register prüfen:** `docs/audit/INTEROP_SOAK_AUDIT.md` enthält F001 (Facade-Coupling-Gap) und ggf. neue Soak-Befunde.

**Damit steht:** Harness-Projekt + Internals-Seam, Ressourcen-Sampler mit Trend-Assert, Audit-Register + Formatter, und der erste vertikale L2-Durchstich (Media-Loopback + Leak-Soak). Nächste Phase: **Phase 1** (weitere Soak-Fokusse: Media-Qualität-Drift, Concurrency, Langzeit-Signaling) — eigener Plan.

## Spec-Abdeckung (Self-Review)

- Harness-Struktur (§4) → Task 1. · Metrik-Sampler + Trend-Assert (§4, §8) → Task 2.
- Audit-Register + Facade-Coupling-Gap (§2, §4.2) → Task 3 (F001) + Task 1. · Ebenen-Fixture L2 (§4.1) → Task 4.
- Soak Ressourcen-Leaks (§8) → Task 4 Leak-Soak. · Kein autonomes Fixen (§3) → Task 4 Step 6 (Befund → Register, kein Test-Fix).
- **Nicht in Phase 0** (spätere Pläne): L3/L4-Fixtures, Non-Happy-Path-Szenarien, Interop/Docker/CI, Media-Qualität-/Concurrency-/Signaling-Soak, Video, `.gitignore`/GHCR.
