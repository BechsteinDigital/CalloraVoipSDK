# PBX-agnostische IPbxFixture-Abstraktion (Phase B.1) — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Die Media-Szenario-Matrix hinter ein schmales `IPbxFixture`-Interface stellen (mit `AsteriskPbxFixture`-Adapter), sodass sie in Phase B.2 „gratis" gegen FreeSWITCH läuft — verhaltensgleich, weiter grün gegen echten Asterisk.

**Architecture:** Ein `IPbxFixture`-Interface kapselt die PBX-Fähigkeiten (Lifecycle, SIP-Adresse, Bridge-Paare, Media-Playback, Logs). `AsteriskPbxFixture` adaptiert die bestehende `AsteriskContainer` (unangetastet). `TwoLegBridgedCall` und die Media-Matrix-Tests arbeiten nur noch gegen `IPbxFixture`; jede Test-Matrix ist eine abstrakte Basisklasse mit einer Asterisk-Subklasse (die `CreatePbx()` liefert).

**Tech Stack:** .NET 8/9/10, xUnit 2.4.2, Testcontainers (`andrius/asterisk:22`), `[DockerRequiredFact]`. Docker lokal vorhanden → measure-first-Verifikation gegen echten Asterisk.

**Design-Spec:** `docs/audit/2026-07-25-pbx-fixture-abstraction-design.md`

**Verhaltensbewahrend:** Keine `src/`-Änderung. Nach jedem Test-Umbau muss die betroffene Matrix weiter grün gegen echten Asterisk laufen; keine Assertion geschwächt.

## Datei-Struktur

- Neu: `tests/CalloraVoipSdk.InteropTests/Pbx/IPbxFixture.cs` — Interface + Records + Enum.
- Neu: `tests/CalloraVoipSdk.InteropTests/Pbx/AsteriskPbxFixture.cs` — Adapter über `AsteriskContainer`.
- Modify: `tests/CalloraVoipSdk.InteropTests/Media/TwoLegBridgedCall.cs` — auf `IPbxFixture` umstellen; `TwoLegProfile` entfällt.
- Modify: die Media-Matrix-Test-Dateien (`Calls/AsteriskTwoLegMediaInteropTests.cs`, `Calls/AsteriskTwoLegDtmfInteropTests.cs`, `Calls/AsteriskTwoLegHoldInteropTests.cs`, `Calls/AsteriskTwoLegTransferInteropTests.cs`, `Soak/AsteriskConcurrentCallSoakTests.cs`) → abstrakte Basisklasse + Asterisk-Subklasse.
- Unangetastet: `Asterisk/AsteriskContainer.cs`, alle Register-/Transport-/Non-Happy-Path-/Inbound-/Codec-Negotiation-/Session-Timer-Tests.

---

### Task 1: `IPbxFixture`-Interface + Records/Enum

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/Pbx/IPbxFixture.cs`

- [ ] **Step 1: Interface + Typen schreiben**

```csharp
namespace CalloraVoipSdk.InteropTests.Pbx;

/// <summary>Ein Fremd-PBX-Peer für die Media-Szenario-Matrix (Asterisk, FreeSWITCH, …).</summary>
public interface IPbxFixture : IAsyncDisposable
{
    /// <summary>Startet den PBX-Container und wartet, bis er SIP-ready ist.</summary>
    Task StartAsync();

    /// <summary>Register-Ziel-Host (Container-Bridge-IP o. ä.). Nur nach <see cref="StartAsync"/> gültig.</summary>
    string SipHost { get; }

    /// <summary>Register-Ziel-UDP-Port.</summary>
    int SipUdpPort { get; }

    /// <summary>
    /// Ein gebrücktes Endpunkt-Paar: Caller- und Callee-Credentials plus die Dial-URI, die der Caller wählt,
    /// damit der PBX ihn an den registrierten Callee brückt. <paramref name="index"/> wählt eines der
    /// bereitgestellten Paare (0-basiert; für den Concurrent-Soak).
    /// </summary>
    PbxBridgePair BridgePair(PbxMediaMode mode, int index);

    /// <summary>Dial-URI einer Extension, die antwortet und Endlos-Media spielt (Transfer-Konsultation).</summary>
    string MediaPlaybackUri { get; }

    /// <summary>Kombinierte Container-Konsolen-Logs (Diagnose).</summary>
    Task<string> GetLogsAsync();
}

/// <summary>Medien-Sicherheitsmodus eines Bridge-Paars.</summary>
public enum PbxMediaMode { Plain, Sdes }

/// <summary>Digest-Credentials eines registrierbaren PBX-Endpunkts.</summary>
public sealed record PbxEndpoint(string Username, string Password);

/// <summary>Ein Caller/Callee-Paar plus die Bridge-Dial-URI, die den Caller an den Callee brückt.</summary>
public sealed record PbxBridgePair(PbxEndpoint Caller, PbxEndpoint Callee, string BridgeDialUri);
```

- [ ] **Step 2: Build verifizieren**

Run: `dotnet build tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true`
Expected: 0 Warnungen, 0 Fehler (reine Typdeklarationen).

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Pbx/IPbxFixture.cs
git commit -m "test(interop): IPbxFixture-Interface + Records für die PBX-agnostische Media-Matrix"
```
(Commit-Message endet — Leerzeile davor — mit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`)

---

### Task 2: `AsteriskPbxFixture`-Adapter

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/Pbx/AsteriskPbxFixture.cs`
- Test: `tests/CalloraVoipSdk.InteropTests/Pbx/AsteriskPbxFixtureTests.cs`

**Kontext:** `AsteriskContainer` (unverändert) exponiert `ContainerIpAddress`, `Username`/`Password` (6001), `BridgeUsername`/`BridgePassword` (6003), `SdesUsername`/`SdesPassword` (6002), `SdesBridgeUsername`/`SdesBridgePassword` (6004), `SoakCallerUser(i)`/`SoakCalleeUser(i)`/`SoakBridgeExtension(i)`/`SoakPassword`, `CallTargetUri(ext)`, `GetConsoleLogsAsync()`, `StartAsync()`, `IAsyncDisposable`, Ctor `AsteriskContainer(int extraBridgePairs = 0)`. **Indexierung:** Paar 0 = das Basis-Paar 6001/6003; Paar i≥1 = Soak-Paar `sc{i-1}`/`se{i-1}`. Der Ctor `AsteriskPbxFixture(bridgePairs)` reicht `extraBridgePairs = bridgePairs - 1` durch.

- [ ] **Step 1: Failing Test schreiben (Adapter registriert Basis-Endpunkt)**

```csharp
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Pbx;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Pbx;

[Trait("Category", "Interop")]
public sealed class AsteriskPbxFixtureTests
{
    [DockerRequiredFact]
    public async Task PlainBridgePair_CallerRegisters_ThroughAdapter()
    {
        await using IPbxFixture pbx = new AsteriskPbxFixture();
        await pbx.StartAsync();
        var pair = pbx.BridgePair(PbxMediaMode.Plain, 0);

        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0", SrtpPolicy = SrtpPolicy.Disabled });
        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = pbx.SipHost, Port = pbx.SipUdpPort,
                Username = pair.Caller.Username, Password = pair.Caller.Password,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.True(reg.IsSuccess, $"Registrierung über den Adapter fehlgeschlagen: {reg.Status}");
    }
}
```

- [ ] **Step 2: Test rot verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~PlainBridgePair_CallerRegisters_ThroughAdapter"`
Expected: FAIL — `AsteriskPbxFixture` existiert nicht.

- [ ] **Step 3: Adapter implementieren**

```csharp
using CalloraVoipSdk.InteropTests.Asterisk;

namespace CalloraVoipSdk.InteropTests.Pbx;

/// <summary>Adaptiert die bestehende <see cref="AsteriskContainer"/> auf <see cref="IPbxFixture"/>.</summary>
public sealed class AsteriskPbxFixture : IPbxFixture
{
    private readonly AsteriskContainer _asterisk;

    /// <summary><paramref name="bridgePairs"/> = Anzahl bereitgestellter Plain-Bridge-Paare (Paar 0 = Basis 6001/6003).</summary>
    public AsteriskPbxFixture(int bridgePairs = 1)
        => _asterisk = new AsteriskContainer(extraBridgePairs: Math.Max(0, bridgePairs - 1));

    public Task StartAsync() => _asterisk.StartAsync();
    public string SipHost => _asterisk.ContainerIpAddress;
    public int SipUdpPort => 5060;
    public string MediaPlaybackUri => _asterisk.CallTargetUri("answer");
    public Task<string> GetLogsAsync() => _asterisk.GetConsoleLogsAsync();

    public PbxBridgePair BridgePair(PbxMediaMode mode, int index) => (mode, index) switch
    {
        (PbxMediaMode.Plain, 0) => new(
            new(_asterisk.Username, _asterisk.Password),
            new(_asterisk.BridgeUsername, _asterisk.BridgePassword),
            _asterisk.CallTargetUri("6003")),
        (PbxMediaMode.Plain, _) => new(
            new(_asterisk.SoakCallerUser(index - 1), _asterisk.SoakPassword),
            new(_asterisk.SoakCalleeUser(index - 1), _asterisk.SoakPassword),
            _asterisk.CallTargetUri(_asterisk.SoakBridgeExtension(index - 1))),
        (PbxMediaMode.Sdes, 0) => new(
            new(_asterisk.SdesUsername, _asterisk.SdesPassword),
            new(_asterisk.SdesBridgeUsername, _asterisk.SdesBridgePassword),
            _asterisk.CallTargetUri("6004")),
        _ => throw new ArgumentOutOfRangeException(nameof(index), $"Kein Bridge-Paar für ({mode}, {index})."),
    };

    public ValueTask DisposeAsync() => _asterisk.DisposeAsync();
}
```

- [ ] **Step 4: Test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~PlainBridgePair_CallerRegisters_ThroughAdapter"`
Expected: PASS (Registrierung über den Adapter gegen echten Asterisk).

- [ ] **Step 5: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Pbx/AsteriskPbxFixture.cs tests/CalloraVoipSdk.InteropTests/Pbx/AsteriskPbxFixtureTests.cs
git commit -m "test(interop): AsteriskPbxFixture-Adapter über AsteriskContainer (IPbxFixture)"
```
(mit Trailer)

---

### Task 3: `TwoLegBridgedCall` auf `IPbxFixture` umstellen

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Media/TwoLegBridgedCall.cs`

**Kontext:** Heute nimmt `StartAsync(AsteriskContainer, TwoLegProfile?)` einen Asterisk + ein `TwoLegProfile` (mit `Plain`/`Sdes`/`CodecMismatch`-Factories, die `AsteriskContainer` binden). Neu: `StartAsync(IPbxFixture, PbxMediaMode, int pairIndex, callerCodecs?, calleeCodecs?)`; das Profil wird aus `pbx.BridgePair(mode, pairIndex)` + Modus + Codec-Pins abgeleitet. `TwoLegProfile` entfällt. `RunBidirectionalMediaAsync`, `StartBidirectionalMedia`/`MediaFlow`, `DialCallerConsultationAsync`, `DisposeAsync` bleiben unverändert (sie hängen nicht an Asterisk).

- [ ] **Step 1: Imports + `TwoLegProfile` ersetzen**

In `TwoLegBridgedCall.cs`: `using CalloraVoipSdk.InteropTests.Asterisk;` → `using CalloraVoipSdk.InteropTests.Pbx;`. Den gesamten `public sealed record TwoLegProfile(...) { … }`-Block (Zeilen 18–43) **entfernen** und stattdessen eine private PCMU-Konstante in die Klasse aufnehmen (siehe Step 2).

- [ ] **Step 2: `RegisterAsync` + `StartAsync` umstellen**

`RegisterAsync` ersetzen:
```csharp
    private static readonly string[] PcmuOnly = { "PCMU" };

    private static async Task<IPhoneLine> RegisterAsync(IPbxFixture pbx, VoipClient client, PbxEndpoint endpoint)
    {
        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = pbx.SipHost,
                Port = pbx.SipUdpPort,
                Username = endpoint.Username,
                Password = endpoint.Password,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        if (!reg.IsSuccess)
            throw new InvalidOperationException($"Registrierung {endpoint.Username} fehlgeschlagen: {reg.Status}");
        return reg.Line!;
    }
```

`StartAsync` ersetzen (Signatur + Profil-Ableitung; die IncomingCall/Accept/Dial-Logik bleibt identisch, nur die Bridge-URI kommt aus dem Paar):
```csharp
    /// <summary>Baut den gebrückten Call über den PBX auf und wartet, bis beide Legs Connected sind.</summary>
    public static async Task<TwoLegBridgedCall> StartAsync(
        IPbxFixture pbx,
        PbxMediaMode mode = PbxMediaMode.Plain,
        int pairIndex = 0,
        IReadOnlyList<string>? callerCodecs = null,
        IReadOnlyList<string>? calleeCodecs = null)
    {
        var pair = pbx.BridgePair(mode, pairIndex);
        var srtp = mode == PbxMediaMode.Sdes ? SrtpPolicy.Required : SrtpPolicy.Disabled;
        var callerClient = NewClient(srtp, callerCodecs ?? PcmuOnly);
        var calleeClient = NewClient(srtp, calleeCodecs ?? PcmuOnly);
        try
        {
            var callerLine = await RegisterAsync(pbx, callerClient, pair.Caller);
            await RegisterAsync(pbx, calleeClient, pair.Callee);

            var calleeTcs = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnIncoming(object? _, IncomingCallEventArgs e) => calleeTcs.TrySetResult(e.Call);
            calleeClient.IncomingCall += OnIncoming;

            var dialTask = callerClient.DialAndWaitUntilConnectedAsync(
                callerLine, pair.BridgeDialUri,
                new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });

            var calleeCall = await calleeTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
            calleeClient.IncomingCall -= OnIncoming;
            await calleeCall.AcceptAsync();

            var dial = await dialTask;
            if (!dial.IsSuccess)
                throw new InvalidOperationException($"Bridged-Dial fehlgeschlagen: {dial.Status}");

            return new TwoLegBridgedCall(callerClient, calleeClient, callerLine, dial.Call!, calleeCall);
        }
        catch
        {
            callerClient.Dispose();
            calleeClient.Dispose();
            throw;
        }
    }
```

- [ ] **Step 3: Build (erwartet Fehler in den noch nicht migrierten Tests)**

Run: `dotnet build tests/CalloraVoipSdk.InteropTests -c Release -f net10.0`
Expected: Kompilerfehler in den 5 Media-Matrix-Test-Dateien (sie rufen noch `StartAsync(asterisk, …)`/`TwoLegProfile.*`). Das ist erwartet — Tasks 4–6 ziehen sie nach. `TwoLegBridgedCall.cs` selbst muss fehlerfrei sein.

- [ ] **Step 4: Commit** (zusammen mit Task 4, sobald die Aufrufer wieder bauen — siehe Task 4 Step 5). Kein separater Commit hier, da der Baum vorübergehend nicht baut.

---

### Task 4: Zwei-Bein-Media-Tests → abstrakte Basis + Asterisk-Subklasse

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs`

**Kontext:** Diese Datei enthält die 8 Zwei-Bein-Media-Tests (`SecondPlainRtpEndpoint_6003_Registers`, `BridgedCall_ConnectsBothLegs`, `BridgedCall_FlowsRtpInBothDirections`, `BridgedCall_PopulatesLocalRtcpQuality`, `BridgedCall_PopulatesRemoteRtcpReport`, `BridgedCall_DeliversMarkedContentEndToEnd`, `SdesBridgedCall_FlowsEncryptedMediaBothDirections`, `MismatchedCodecBridgedCall_StillFlowsViaTranscoding`) + `LongestContiguousRun`. Umbau: Klasse `abstract`, `SecondPlainRtpEndpoint_6003_Registers` entfällt (Asterisk-endpoint-spezifisch, durch `AsteriskPbxFixtureTests` ersetzt), Fixture-Aufbau über `CreatePbx()`, Aufrufe von `TwoLegBridgedCall.StartAsync(asterisk, …)`/`TwoLegProfile.*` auf die neue Signatur.

- [ ] **Step 1: Klasse in abstrakte Basis umbauen**

Header + Klassendeklaration ersetzen: `using CalloraVoipSdk.InteropTests.Asterisk;` → `using CalloraVoipSdk.InteropTests.Pbx;`; `[Trait("Category", "Interop")] public sealed class AsteriskTwoLegMediaInteropTests` → `public abstract class TwoLegMediaMatrix`. Die private `NewClient()`-Hilfe + der Smoke-Test `SecondPlainRtpEndpoint_6003_Registers` (nutzt `asterisk.BridgeUsername` direkt) werden **entfernt** (durch `AsteriskPbxFixtureTests` aus Task 2 abgedeckt). Ganz oben in die Klasse:
```csharp
    protected abstract IPbxFixture CreatePbx(int bridgePairs = 1);
```

- [ ] **Step 2: Jeden Test von `AsteriskContainer` auf `CreatePbx()` umstellen**

Muster für JEDEN verbleibenden Test — die drei ersten Zeilen
```csharp
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);
```
werden zu
```csharp
        await using var pbx = CreatePbx();
        await pbx.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(pbx);
```
Die drei Varianten-Aufrufe entsprechend:
- `TwoLegBridgedCall.StartAsync(asterisk, TwoLegProfile.Sdes(asterisk))` → `TwoLegBridgedCall.StartAsync(pbx, PbxMediaMode.Sdes)` (in `SdesBridgedCall_FlowsEncryptedMediaBothDirections`).
- `TwoLegBridgedCall.StartAsync(asterisk, TwoLegProfile.CodecMismatch(asterisk))` → `TwoLegBridgedCall.StartAsync(pbx, callerCodecs: new[] { "G722" })` (in `MismatchedCodecBridgedCall_StillFlowsViaTranscoding`).
Alle Assertions, Laufzeiten, Poll-Deadlines, `LongestContiguousRun`, `[DockerRequiredFact]` **unverändert**.

- [ ] **Step 3: Asterisk-Subklasse anlegen** (ans Dateiende, gleiche Datei)

```csharp
/// <summary>Fährt die Zwei-Bein-Media-Matrix gegen einen echten Asterisk.</summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegMediaMatrix : TwoLegMediaMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new AsteriskPbxFixture(bridgePairs);
}
```

- [ ] **Step 4: Build + Tests grün verifizieren**

Run: `dotnet build tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true` → 0 Warnungen (Task 3 + diese Datei bauen jetzt; die DTMF/Hold/Transfer/Soak-Dateien bauen noch NICHT — falls der Build an denen scheitert, ist das erwartet bis Task 5/6; um Task 4 isoliert grün zu sehen, ggf. erst nach Task 6 die volle Suite bauen. Alternativ Task 4–6 als eine Einheit umsetzen und am Ende bauen).
Run (nach Task 6, wenn alles baut): `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~AsteriskTwoLegMediaMatrix"`
Expected: 7/7 grün gegen echten Asterisk (verhaltensgleich; der 8. „Registers"-Smoke wurde nach `AsteriskPbxFixtureTests` verschoben).

- [ ] **Step 5: Commit** (Task 3 + Task 4 zusammen, da der Baum dazwischen nicht baut)

```bash
git add tests/CalloraVoipSdk.InteropTests/Media/TwoLegBridgedCall.cs tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs
git commit -m "test(interop): TwoLegBridgedCall + Zwei-Bein-Media-Matrix auf IPbxFixture (abstrakte Basis + Asterisk-Subklasse)"
```
(mit Trailer)

---

### Task 5: DTMF / Hold / Attended-Transfer → abstrakte Basis + Asterisk-Subklasse

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegDtmfInteropTests.cs`
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegHoldInteropTests.cs`
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegTransferInteropTests.cs`

**Kontext:** Jede Datei hat genau eine Test-Methode, die eine `TwoLegBridgedCall`-Bridge aufbaut. DTMF/Hold nutzen `TwoLegBridgedCall.StartAsync(asterisk)` (Plain). Transfer nutzt zusätzlich `bridged.DialCallerConsultationAsync(asterisk.CallTargetUri("answer"), …)` → jetzt `pbx.MediaPlaybackUri`.

- [ ] **Step 1: Jede der drei Dateien nach demselben Muster umbauen**

Pro Datei: `using CalloraVoipSdk.InteropTests.Asterisk;` → `using CalloraVoipSdk.InteropTests.Pbx;`; Klasse `[Trait("Category","Interop")] public sealed class Asterisk…InteropTests` → `public abstract class …Matrix` mit `protected abstract IPbxFixture CreatePbx(int bridgePairs = 1);`; im Test die drei Aufbau-Zeilen wie in Task 4 auf `CreatePbx()`/`pbx` umstellen. In `AsteriskTwoLegTransferInteropTests`: `asterisk.CallTargetUri("answer")` → `pbx.MediaPlaybackUri`. Assertions/Timings/`[DockerRequiredFact]` unverändert.

Konkrete Klassennamen:
- `AsteriskTwoLegDtmfInteropTests` → abstrakte Basis `TwoLegDtmfMatrix` + Subklasse `AsteriskTwoLegDtmfMatrix`.
- `AsteriskTwoLegHoldInteropTests` → `TwoLegHoldMatrix` + `AsteriskTwoLegHoldMatrix`.
- `AsteriskTwoLegTransferInteropTests` → `TwoLegTransferMatrix` + `AsteriskTwoLegTransferMatrix`.

Subklasse (ans jeweilige Dateiende):
```csharp
[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegDtmfMatrix : TwoLegDtmfMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new AsteriskPbxFixture(bridgePairs);
}
```
(analog für Hold/Transfer).

- [ ] **Step 2: Build + Tests grün**

Run: `dotnet build tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true` (die Soak-Datei baut noch nicht bis Task 6).
Run (nach Task 6): `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~AsteriskTwoLegDtmfMatrix|FullyQualifiedName~AsteriskTwoLegHoldMatrix|FullyQualifiedName~AsteriskTwoLegTransferMatrix"`
Expected: 3/3 grün gegen echten Asterisk (verhaltensgleich).

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegDtmfInteropTests.cs tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegHoldInteropTests.cs tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegTransferInteropTests.cs
git commit -m "test(interop): DTMF/Hold/Transfer-Matrix auf IPbxFixture (abstrakte Basis + Asterisk-Subklasse)"
```
(mit Trailer)

---

### Task 6: Concurrent-Soak → abstrakte Basis + Asterisk-Subklasse

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Soak/AsteriskConcurrentCallSoakTests.cs`

**Kontext:** Der Soak baut `new AsteriskContainer(extraBridgePairs: callCount)` und iteriert `SoakCallerUser(i)`/… über `callCount` Paare. Neu: `CreatePbx(callCount)` (→ `new AsteriskPbxFixture(callCount)`, das intern `extraBridgePairs: callCount-1` setzt, Paar 0 = Basis) und je Call `TwoLegBridgedCall.StartAsync(pbx, PbxMediaMode.Plain, pairIndex: i)` für i in 0..callCount-1.

- [ ] **Step 1: Umbau**

`using …Asterisk;` → `using …Pbx;`. Klasse `[Trait("Category","SoakLong")] public sealed class AsteriskConcurrentCallSoakTests` → `public abstract class ConcurrentCallSoakMatrix` mit `protected abstract IPbxFixture CreatePbx(int bridgePairs = 1);`. In `RunConcurrentSoakAsync`: `await using var asterisk = new AsteriskContainer(extraBridgePairs: callCount);` → `await using var pbx = CreatePbx(callCount);`. In `RunOneAsync(...)`: statt des per-Paar-`TwoLegProfile` aus `SoakCallerUser`/… den Aufruf `await using var bridged = await TwoLegBridgedCall.StartAsync(pbx, PbxMediaMode.Plain, pairIndex: i);`. Die `PacketsReceived`-Assertions + Stagger + Fehler-Sammlung unverändert. `CallCountFromEnv` unverändert.

- [ ] **Step 2: Asterisk-Subklasse** (ans Dateiende)

```csharp
[Trait("Category", "SoakLong")]
public sealed class AsteriskConcurrentCallSoakMatrix : ConcurrentCallSoakMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new AsteriskPbxFixture(bridgePairs);
}
```
Hinweis: Die `[Trait]`-Zuordnung der Short/Long-Methoden bleibt in der Basisklasse (Short `[DockerRequiredFact, Trait("Category","Interop")]`, Long `[DockerRequiredFact, Trait("Category","SoakLong")]`) — xUnit erbt Method-Traits in die Subklasse.

- [ ] **Step 3: Build (volle Suite baut jetzt) + Soak grün**

Run: `dotnet build tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true`
Expected: 0 Warnungen, 0 Fehler (alle Aufrufer migriert).
Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~ConcurrentBridgedCalls_Short"` + `INTEROP_SOAK_CONCURRENT_CALLS=20 dotnet test … --filter "FullyQualifiedName~ConcurrentBridgedCalls_Long"`
Expected: beide grün (N=4 / N=20 parallele Bridged-Calls über den Adapter).

- [ ] **Step 4: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Soak/AsteriskConcurrentCallSoakTests.cs
git commit -m "test(interop): Concurrent-Soak-Matrix auf IPbxFixture (abstrakte Basis + Asterisk-Subklasse)"
```
(mit Trailer)

---

### Task 7: Voll-Regression

**Files:** keine (nur Verifikation).

- [ ] **Step 1: Volle InteropTests-Suite gegen echten Asterisk**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0`
Expected: Alles grün (Media-Matrix über die Adapter + alle nicht-migrierten Register-/Transport-/Non-Happy-Path-/Inbound-/Codec-Negotiation-/Session-Timer-Tests unverändert). Die Testzahl bleibt gleich oder ±0 (der `SecondPlainRtpEndpoint_6003_Registers`-Smoke wurde nach `AsteriskPbxFixtureTests` verschoben — netto gleich).

- [ ] **Step 2: `git diff` gegen `src/` prüfen**

Run: `git diff --stat origin/main..HEAD -- src/`
Expected: leer (keine `src/`-Änderung).

- [ ] **Step 3: Commit** (nur falls Step 1/2 eine Korrektur nötig machten; sonst kein Commit).

---

## Abschluss

Nach Task 7: die Media-Matrix läuft über `IPbxFixture`/`AsteriskPbxFixture`, verhaltensgleich. **Phase B.2 (FreeSWITCH)** fügt dann nur `FreeSwitchPbxFixture : IPbxFixture` + je eine `FreeSwitch…Matrix`-Subklasse pro Matrix-Basisklasse hinzu — die ganze Matrix läuft „gratis" gegen FreeSWITCH. Branch `feat/interop-pbx-abstraction` per `superpowers:finishing-a-development-branch` abschließen (PR-Erstellung durch den User).
