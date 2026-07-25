# FreeSWITCH `IPbxFixture`-Implementierung (Phase B.2) — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eine zweite `IPbxFixture`-Implementierung gegen echtes FreeSWITCH bauen, sodass die Zwei-Bein-Media-Szenario-Matrix aus B.1 (Media/DTMF/Hold/Transfer/Soak) „gratis" auch gegen FreeSWITCH läuft.

**Architecture:** `FreeSwitchContainer` (safarov/freeswitch:latest + injizierte Directory-/Dialplan-XML via Config-Overlay, log-basierte Wait-Strategy) + `FreeSwitchPbxFixture : IPbxFixture` (dünnes Mapping, spiegelt `AsteriskPbxFixture`) + 5 `FreeSwitch…Matrix`-Subklassen (Einzeiler). FreeSWITCH ist B2BUA → immer im Medienpfad (kein `direct_media`).

**Tech Stack:** .NET 8/9/10, xUnit 2.4.2, Testcontainers 4.13.0, `safarov/freeswitch:latest`, `[DockerRequiredFact]`. Docker lokal vorhanden → measure-first gegen echtes FreeSWITCH.

**Spec:** `docs/audit/2026-07-25-freeswitch-pbx-fixture-design.md`

**Vorbewiesen (Spike):** Image bootet öffentlich, Sofia-`internal` bindet 5060 UDP+TCP über die Bridge-IP; Ready-Log-Zeile = `MSG Thread 0 Started` (sofia.c:2290), erscheint ~4 s nach Start.

**Verhaltensbewahrend:** keine `IPbxFixture`-/Basisklassen-Änderung (B.1 fix), keine `src/`-Änderung, `AsteriskContainer`/Asterisk-Subklassen unberührt. Alle FreeSwitch-Tests tragen `Category=InteropFreeSwitch` → **lokal-first, aus dem PR-CI-Gate ausgeschlossen** (s. Task 7).

## Datei-Struktur

- Neu: `tests/CalloraVoipSdk.InteropTests/FreeSwitch/FreeSwitchContainer.cs` — Container + XML-Config-Generierung + Wait-Strategy + Endpoint-Accessoren.
- Neu: `tests/CalloraVoipSdk.InteropTests/Pbx/FreeSwitchPbxFixture.cs` — `IPbxFixture`-Adapter über `FreeSwitchContainer`.
- Neu: `tests/CalloraVoipSdk.InteropTests/Pbx/FreeSwitchPbxFixtureTests.cs` — Register-Smoke.
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs`, `AsteriskTwoLegDtmfInteropTests.cs`, `AsteriskTwoLegHoldInteropTests.cs`, `AsteriskTwoLegTransferInteropTests.cs`, `Soak/AsteriskConcurrentCallSoakTests.cs` — je eine `FreeSwitch…Matrix`-Subklasse ans Dateiende (die abstrakten Basen sind bereits da).
- Modify: `.github/workflows/ci.yml` — Interop-Filter um `&Category!=InteropFreeSwitch` erweitern (Task 7).
- Modify: `docs/audit/INTEROP_SOAK_AUDIT.md` — Coverage-Notiz FreeSWITCH.

---

### Task 1: `FreeSwitchContainer` (Config-Overlay + Wait-Strategy)

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/FreeSwitch/FreeSwitchContainer.cs`

**Kontext:** Spiegelt `AsteriskContainer` (`tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainer.cs` — als Vorbild lesen). Statt PJSIP-`.conf` generieren wir FreeSWITCH-**XML** und mounten sie per `WithResourceMapping(FileInfo, FileInfo)` (reguläre Temp-Dateien — die Byte-Array-Variante wird ignoriert). Wir überlagern nur Directory (User) + Dialplan; der Rest der Vanilla-Config bleibt. Zugriff über die Container-Bridge-IP:5060 (kein Port-Mapping nötig, Linux). Ready-Signal = Log-Zeile `MSG Thread 0 Started`.

- [ ] **Step 1: Datei mit Config-Generierung + Container-Build schreiben**

```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CalloraVoipSdk.InteropTests.FreeSwitch;

/// <summary>
/// Startet einen FreeSWITCH-Container (safarov/freeswitch:latest) mit injizierter Directory- und
/// Dialplan-XML (Config-Overlay über die Vanilla-Config): Endpoints 6001 (Plain, Multi-Codec),
/// 6003 (Plain, PCMU-only) als Zwei-Bein-Bridge-Paar, 6002/6004 (SRTP-SDES), plus optionale
/// Soak-Paare sc{i}/se{i}. FreeSWITCH ist B2BUA → immer im Medienpfad (kein direct_media nötig).
/// Nur für Interop-Tests. Zugriff über die Container-Bridge-IP:5060 (Linux).
/// </summary>
public sealed class FreeSwitchContainer : IAsyncDisposable
{
    // FreeSWITCH-Domain in der Vanilla-Config = $${domain} = Container-IP. Wir referenzieren sie im
    // Dialplan als $${domain}; Registrierungen landen via force-register-domain in dieser Domain.
    private const string DirectoryPath = "/etc/freeswitch/directory/default/zzz_callora.xml";
    private const string DialplanPath = "/etc/freeswitch/dialplan/default.xml";

    private readonly IContainer _container;
    private readonly FileInfo _directoryFile;
    private readonly FileInfo _dialplanFile;

    /// <summary>Erstellt (noch nicht gestartet) den FreeSWITCH-Container.</summary>
    /// <param name="extraBridgePairs">Zusätzliche Plain-RTP-Paare sc{i}/se{i} für den Soak (0 = Basis).</param>
    public FreeSwitchContainer(int extraBridgePairs = 0)
    {
        _directoryFile = WriteTemp(BuildDirectoryXml(extraBridgePairs));
        _dialplanFile = WriteTemp(BuildDialplanXml(extraBridgePairs));

        _container = new ContainerBuilder("safarov/freeswitch:latest")
            .WithResourceMapping(_directoryFile, new FileInfo(DirectoryPath))
            .WithResourceMapping(_dialplanFile, new FileInfo(DialplanPath))
            .WithExposedPort("5060/udp")
            .WithPortBinding("5060/udp", assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("MSG Thread 0 Started"))
            .Build();
    }

    private static FileInfo WriteTemp(string content)
    {
        var f = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(f.FullName, content);
        return f;
    }

    // ── Directory (registrierbare User) ──────────────────────────────────────────────────────────
    // Ein <include> mit mehreren <user>. Domain = die des einschließenden directory/default.xml
    // (= $${domain} = Container-IP). PCMU-Pin via absolute_codec_string; SDES via rtp_secure_media.
    private static string BuildDirectoryXml(int extraBridgePairs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<include>");
        AppendUser(sb, "6001", codec: null, sdes: false);   // Caller Plain, Multi-Codec (SDK pinnt)
        AppendUser(sb, "6003", codec: "PCMU", sdes: false);  // Callee Plain, PCMU-only
        AppendUser(sb, "6002", codec: null, sdes: true);     // Caller SDES
        AppendUser(sb, "6004", codec: "PCMU", sdes: true);   // Callee SDES, PCMU-only
        for (var i = 0; i < extraBridgePairs; i++)
        {
            AppendUser(sb, $"sc{i}", codec: "PCMU", sdes: false);
            AppendUser(sb, $"se{i}", codec: "PCMU", sdes: false);
        }
        sb.AppendLine("</include>");
        return sb.ToString();
    }

    private static void AppendUser(System.Text.StringBuilder sb, string id, string? codec, bool sdes)
    {
        sb.AppendLine($"  <user id=\"{id}\">");
        sb.AppendLine("    <params><param name=\"password\" value=\"secret\"/></params>");
        sb.AppendLine("    <variables>");
        sb.AppendLine("      <variable name=\"user_context\" value=\"default\"/>");
        if (codec is not null)
            sb.AppendLine($"      <variable name=\"absolute_codec_string\" value=\"{codec}\"/>");
        if (sdes)
            sb.AppendLine("      <variable name=\"rtp_secure_media\" value=\"mandatory\"/>");
        sb.AppendLine("    </variables>");
        sb.AppendLine("  </user>");
    }

    // ── Dialplan (Bridge + Media-Playback) ───────────────────────────────────────────────────────
    // Ersetzt die Vanilla-default.xml. Caller wählt die Callee-Extension → bridge an den User.
    // "answer" spielt endlosen 1004-Hz-Ton (Milliwatt-Äquivalent) für die Transfer-Konsultation.
    private static string BuildDialplanXml(int extraBridgePairs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<include>");
        sb.AppendLine("  <context name=\"default\">");
        AppendBridge(sb, "6003");
        AppendBridge(sb, "6004");
        sb.AppendLine("    <extension name=\"callora-media-playback\">");
        sb.AppendLine("      <condition field=\"destination_number\" expression=\"^answer$\">");
        sb.AppendLine("        <action application=\"answer\"/>");
        sb.AppendLine("        <action application=\"playback\" data=\"tone_stream://%(3600000,0,1004)\"/>");
        sb.AppendLine("      </condition>");
        sb.AppendLine("    </extension>");
        for (var i = 0; i < extraBridgePairs; i++)
            AppendBridge(sb, $"se{i}");
        sb.AppendLine("  </context>");
        sb.AppendLine("</include>");
        return sb.ToString();
    }

    private static void AppendBridge(System.Text.StringBuilder sb, string callee)
    {
        sb.AppendLine($"    <extension name=\"callora-bridge-{callee}\">");
        sb.AppendLine($"      <condition field=\"destination_number\" expression=\"^{callee}$\">");
        sb.AppendLine($"        <action application=\"bridge\" data=\"user/{callee}@$${{domain}}\"/>");
        sb.AppendLine("      </condition>");
        sb.AppendLine("    </extension>");
    }

    // ── IPbxFixture-relevante Accessoren ─────────────────────────────────────────────────────────
    public Task StartAsync() => _container.StartAsync();
    public string ContainerIpAddress => _container.IpAddress;

    public string Username => "6001";
    public string Password => "secret";
    public string BridgeUsername => "6003";
    public string BridgePassword => "secret";
    public string SdesUsername => "6002";
    public string SdesPassword => "secret";
    public string SdesBridgeUsername => "6004";
    public string SdesBridgePassword => "secret";
    public string SoakPassword => "secret";
    public string SoakCallerUser(int i) => $"sc{i}";
    public string SoakCalleeUser(int i) => $"se{i}";
    public string SoakBridgeExtension(int i) => $"se{i}";

    /// <summary>Ziel-Request-URI für eine Dialplan-Extension (Bridge-Callee oder "answer").</summary>
    public string CallTargetUri(string extension) => $"sip:{extension}@{ContainerIpAddress}:5060";

    public async Task<string> GetConsoleLogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
        return stdout + "\n" + stderr;
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
        try { _directoryFile.Delete(); } catch { /* best effort */ }
        try { _dialplanFile.Delete(); } catch { /* best effort */ }
    }
}
```

- [ ] **Step 2: Build verifizieren**

Run: `dotnet build tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true`
Expected: 0 Warnungen / 0 Fehler.

- [ ] **Step 3: Commit** (zusammen mit Task 2, da `FreeSwitchContainer` erst durch den Register-Smoke getestet wird — kein eigener Test hier).

Kein separater Commit; s. Task 2 Step 5.

---

### Task 2: `FreeSwitchPbxFixture`-Adapter + Register-Smoke

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/Pbx/FreeSwitchPbxFixture.cs`
- Test: `tests/CalloraVoipSdk.InteropTests/Pbx/FreeSwitchPbxFixtureTests.cs`

**Kontext:** Struktur identisch zu `AsteriskPbxFixture` (lesen). Der Register-Smoke ist die **measure-first-Kernvalidierung**: registriert der SDK gegen echtes FreeSWITCH über den Adapter? Deckt Directory-XML + Domain + internal-Profil ab.

- [ ] **Step 1: Failing Test schreiben**

```csharp
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Pbx;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Pbx;

[Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchPbxFixtureTests
{
    [DockerRequiredFact]
    public async Task PlainBridgePair_CallerRegisters_ThroughAdapter()
    {
        await using IPbxFixture pbx = new FreeSwitchPbxFixture();
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

        Assert.True(reg.IsSuccess, $"FreeSWITCH-Registrierung über den Adapter fehlgeschlagen: {reg.Status}");
    }
}
```

- [ ] **Step 2: Test rot verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~PlainBridgePair_CallerRegisters_ThroughAdapter&Category=InteropFreeSwitch"`
Expected: FAIL — `FreeSwitchPbxFixture` existiert nicht.

- [ ] **Step 3: Adapter implementieren**

```csharp
using CalloraVoipSdk.InteropTests.FreeSwitch;

namespace CalloraVoipSdk.InteropTests.Pbx;

/// <summary>Adaptiert <see cref="FreeSwitchContainer"/> auf <see cref="IPbxFixture"/> (spiegelt AsteriskPbxFixture).</summary>
public sealed class FreeSwitchPbxFixture : IPbxFixture
{
    private readonly FreeSwitchContainer _fs;

    public FreeSwitchPbxFixture(int bridgePairs = 1)
        => _fs = new FreeSwitchContainer(extraBridgePairs: Math.Max(0, bridgePairs - 1));

    public Task StartAsync() => _fs.StartAsync();
    public string SipHost => _fs.ContainerIpAddress;
    public int SipUdpPort => 5060;
    public string MediaPlaybackUri => _fs.CallTargetUri("answer");
    public Task<string> GetLogsAsync() => _fs.GetConsoleLogsAsync();

    public PbxBridgePair BridgePair(PbxMediaMode mode, int index) => (mode, index) switch
    {
        (PbxMediaMode.Plain, 0) => new(
            new(_fs.Username, _fs.Password),
            new(_fs.BridgeUsername, _fs.BridgePassword),
            _fs.CallTargetUri("6003")),
        (PbxMediaMode.Plain, _) => new(
            new(_fs.SoakCallerUser(index - 1), _fs.SoakPassword),
            new(_fs.SoakCalleeUser(index - 1), _fs.SoakPassword),
            _fs.CallTargetUri(_fs.SoakBridgeExtension(index - 1))),
        (PbxMediaMode.Sdes, 0) => new(
            new(_fs.SdesUsername, _fs.SdesPassword),
            new(_fs.SdesBridgeUsername, _fs.SdesBridgePassword),
            _fs.CallTargetUri("6004")),
        _ => throw new ArgumentOutOfRangeException(nameof(index), $"Kein Bridge-Paar für ({mode}, {index})."),
    };

    public ValueTask DisposeAsync() => _fs.DisposeAsync();
}
```

- [ ] **Step 4: Test grün verifizieren (measure-first)**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~PlainBridgePair_CallerRegisters_ThroughAdapter&Category=InteropFreeSwitch"`
Expected: PASS.
**Falls FAIL (measure-first-Iteration, KEINE Assertion schwächen):** die Container-Logs via `docker logs` prüfen. Wahrscheinliche Ursachen + Fixes am `FreeSwitchContainer` (nicht am Test):
- Registrierung abgelehnt (403/401-Loop): Domain-Mismatch — der User `6001` muss in der Domain `$${domain}` (= Container-IP) auflösbar sein; prüfen, ob `directory/default/zzz_callora.xml` von `directory/default.xml` per `*.xml`-Glob eingezogen wird (Vanilla tut das). Ggf. `user_context`/Domain angleichen.
- Wait feuert zu früh → REGISTER-Timeout: nach der Log-Wait einen kurzen Settle-Delay ergänzen ODER auf eine spätere Log-Zeile warten (Logs inspizieren). Der 20-s-ConnectAsync-Timeout deckt SIP-Retransmits ab.
- XML-Parse-Fehler: Container-Log zeigt `[ERR] ... xml` — die generierte XML gegen `docker exec … cat` prüfen.

- [ ] **Step 5: Commit** (Task 1 + Task 2)

```bash
git add tests/CalloraVoipSdk.InteropTests/FreeSwitch/FreeSwitchContainer.cs tests/CalloraVoipSdk.InteropTests/Pbx/FreeSwitchPbxFixture.cs tests/CalloraVoipSdk.InteropTests/Pbx/FreeSwitchPbxFixtureTests.cs
git commit -m "test(interop): FreeSwitchContainer + FreeSwitchPbxFixture-Adapter (Register-Smoke gegen echtes FreeSWITCH)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Plain-Media-Matrix gegen FreeSWITCH

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs`

**Kontext:** Die abstrakte Basis `TwoLegMediaMatrix` (aus B.1) enthält die 7 Media-Tests + den lokal-only SDES-Content-Test (`Category=InteropLocalMedia` auf der Methode). Wir fügen NUR eine FreeSWITCH-Subklasse ans Dateiende. Sie erbt alle Tests. Der SDES-Content-Test bleibt via geerbtem `InteropLocalMedia` lokal; die Subklasse trägt zusätzlich `InteropFreeSwitch`.

- [ ] **Step 1: FreeSWITCH-Subklasse ans Dateiende ergänzen**

```csharp
/// <summary>Fährt die Zwei-Bein-Media-Matrix gegen echtes FreeSWITCH.</summary>
[Trait("Category", "Interop"), Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchTwoLegMediaMatrix : TwoLegMediaMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new FreeSwitchPbxFixture(bridgePairs);
}
```
(Import ergänzen falls nötig: `using CalloraVoipSdk.InteropTests.Pbx;` ist bereits vorhanden.)

- [ ] **Step 2: Media-Matrix gegen FreeSWITCH grün verifizieren (measure-first)**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~FreeSwitchTwoLegMediaMatrix"`
Expected: alle Tests grün (inkl. der lokal-only SDES-Content-Test, da lokal ausgeführt).
**Measure-first-Iteration bei FAIL (Fix am `FreeSwitchContainer`, nicht am Test):**
- Kein bidir. RTP / kein Content: prüfen, dass der Dialplan `bypass_media` NICHT setzt (B2BUA muss im Medienpfad bleiben) und `absolute_codec_string=PCMU` auf 6003 greift (PCMU-Passthrough für die byte-exakte Verifikation). Codec-Mismatch-Test erwartet Transcoding (Caller pinnt G722 clientseitig) → FreeSWITCH transcodiert (immer im Pfad).
- RTCP-Quality-Felder null: FreeSWITCH sendet RTCP; falls Felder leer, Container-Logs/Timing prüfen (die Tests pollen bereits mit Deadline aus B.1).

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs
git commit -m "test(interop): FreeSwitchTwoLegMediaMatrix — Zwei-Bein-Media gegen echtes FreeSWITCH

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: DTMF / Hold / Transfer gegen FreeSWITCH

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegDtmfInteropTests.cs`
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegHoldInteropTests.cs`
- Modify: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegTransferInteropTests.cs`

**Kontext:** Je eine FreeSWITCH-Subklasse ans Dateiende (die abstrakten Basen `TwoLegDtmfMatrix`/`TwoLegHoldMatrix`/`TwoLegTransferMatrix` existieren aus B.1). Transfer nutzt `pbx.MediaPlaybackUri` (→ FreeSWITCH-`answer`-Extension mit Endlos-Ton).

- [ ] **Step 1: Drei Subklassen ergänzen**

In `AsteriskTwoLegDtmfInteropTests.cs`:
```csharp
[Trait("Category", "Interop"), Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchTwoLegDtmfMatrix : TwoLegDtmfMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new FreeSwitchPbxFixture(bridgePairs);
}
```
In `AsteriskTwoLegHoldInteropTests.cs`:
```csharp
[Trait("Category", "Interop"), Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchTwoLegHoldMatrix : TwoLegHoldMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new FreeSwitchPbxFixture(bridgePairs);
}
```
In `AsteriskTwoLegTransferInteropTests.cs`:
```csharp
[Trait("Category", "Interop"), Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchTwoLegTransferMatrix : TwoLegTransferMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new FreeSwitchPbxFixture(bridgePairs);
}
```
(In jeder Datei sind `using CalloraVoipSdk.InteropTests.Pbx;` bereits vorhanden.)

- [ ] **Step 2: Grün verifizieren (measure-first)**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~FreeSwitchTwoLegDtmfMatrix|FullyQualifiedName~FreeSwitchTwoLegHoldMatrix|FullyQualifiedName~FreeSwitchTwoLegTransferMatrix"`
Expected: 3/3 grün.
**Measure-first bei FAIL (Fix am Fixture/Dialplan, nicht am Test):**
- DTMF: FreeSWITCH relayt RFC-4733-telephone-event durch die Bridge standardmäßig; falls die Ziffern nicht ankommen, prüfen, ob die Bridge `RFC2833` durchreicht (Vanilla-Default tut das).
- Hold: re-INVITE-Hold wird von FreeSWITCH gehandhabt; Media-Resume nach Unhold prüfen.
- Transfer: Attended-Transfer via REFER/Replaces — FreeSWITCH unterstützt es; falls der Callee nach Transfer kein Media vom neuen Ziel (`answer`-Ton) empfängt, prüfen, ob die `answer`-Extension antwortet + `tone_stream` spielt (Container-Log).

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegDtmfInteropTests.cs tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegHoldInteropTests.cs tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegTransferInteropTests.cs
git commit -m "test(interop): FreeSwitch DTMF/Hold/Transfer-Matrix gegen echtes FreeSWITCH

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Concurrent-Soak gegen FreeSWITCH

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Soak/AsteriskConcurrentCallSoakTests.cs`

**Kontext:** FreeSWITCH-Subklasse der abstrakten `ConcurrentCallSoakMatrix` (aus B.1). Provisioning via `CreatePbx(callCount)` → `FreeSwitchPbxFixture(callCount)` → `FreeSwitchContainer(extraBridgePairs: callCount-1)`. Die Basis-Methoden-Traits (Short `Category=Interop`, Long `Category=SoakLong`) werden vererbt; die Subklasse fügt `InteropFreeSwitch` hinzu.

- [ ] **Step 1: Subklasse ergänzen**

```csharp
[Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchConcurrentCallSoakMatrix : ConcurrentCallSoakMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new FreeSwitchPbxFixture(bridgePairs);
}
```
Trait-Logik: KEIN class-level `Category=Interop`/`SoakLong` (die Short/Long-Methoden-Traits `Category=Interop` bzw. `Category=SoakLong` werden aus der Basis vererbt, wie bei der Asterisk-Soak-Subklasse, die gar kein class-level Trait trägt). Das class-level `InteropFreeSwitch` wird aber ergänzt, damit der Short-Soak (geerbtes `Category=Interop`) im PR-CI-Interop-Gate via `Category!=InteropFreeSwitch` ausgeschlossen wird (Task 7). Ergebnis: Short = {Interop, InteropFreeSwitch}, Long = {SoakLong, InteropFreeSwitch}; lokal via `Category=InteropFreeSwitch` (Short) bzw. `Category=InteropFreeSwitch&Category=SoakLong` (Long) laufbar.

- [ ] **Step 2: Short-Soak gegen FreeSWITCH grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "FullyQualifiedName~FreeSwitchConcurrentCallSoakMatrix&FullyQualifiedName~ConcurrentBridgedCalls_Short"`
Expected: grün (N=4 parallele Bridged-Calls über FreeSWITCH).
Optional Long: `INTEROP_SOAK_CONCURRENT_CALLS=20 dotnet test … --filter "FullyQualifiedName~FreeSwitchConcurrentCallSoakMatrix&FullyQualifiedName~ConcurrentBridgedCalls_Long"`.
**Measure-first bei FAIL:** prüfen, dass alle `sc{i}`/`se{i}` im Directory stehen und die `se{i}`-Bridge-Extensions im Dialplan (Container-Log auf Registrierungs-/Dial-Fehler).

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Soak/AsteriskConcurrentCallSoakTests.cs
git commit -m "test(interop): FreeSwitch Concurrent-Soak-Matrix gegen echtes FreeSWITCH

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: CI-Ausschluss + Register-Notiz

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `docs/audit/INTEROP_SOAK_AUDIT.md`

**Kontext:** Die FreeSwitch-Subklassen tragen `Category=Interop` (→ aus dem Haupt-Unit-Job ausgeschlossen wie Asterisk) UND `Category=InteropFreeSwitch`. Der PR-CI-**Interop**-Job (`Category=Interop`) würde sie sonst matchen → wir schließen `InteropFreeSwitch` dort aus (lokal-first).

- [ ] **Step 1: CI-Interop-Filter erweitern**

In `.github/workflows/ci.yml` den Interop-Test-Schritt (aktuell `--filter "Category=Interop&Category!=InteropLocalMedia"`) ändern zu:
```
--filter "Category=Interop&Category!=InteropLocalMedia&Category!=InteropFreeSwitch"
```
(Kommentar ergänzen: FreeSWITCH lokal-first, noch nicht im PR-CI-Gate.)

- [ ] **Step 2: Discovery ohne Docker verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "Category=Interop&Category!=InteropLocalMedia&Category!=InteropFreeSwitch" --list-tests`
Expected: KEIN `FreeSwitch…`-Test erscheint; die Asterisk-Tests weiterhin schon.
Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "Category=InteropFreeSwitch" --list-tests`
Expected: alle `FreeSwitch…Matrix`-Tests + `FreeSwitchPbxFixtureTests` erscheinen.

- [ ] **Step 3: Register-Notiz ergänzen**

In `docs/audit/INTEROP_SOAK_AUDIT.md` eine Coverage-Notiz „FreeSWITCH-Interop (Phase B.2)" anfügen: zweiter Fremd-Stack über dieselbe `IPbxFixture`-Matrix (safarov/freeswitch:latest, Config-Overlay, B2BUA), lokal-first (`Category=InteropFreeSwitch` aus dem PR-CI-Gate), abgedeckte Szenarien (Media/DTMF/Hold/Transfer/Soak), etwaige measure-first-Funde.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml docs/audit/INTEROP_SOAK_AUDIT.md
git commit -m "ci+docs(interop): FreeSWITCH lokal-first aus PR-CI-Gate ausschließen + Register-Notiz

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Voll-Regression

**Files:** keine (nur Verifikation).

- [ ] **Step 1: Alle FreeSWITCH-Matrizen lokal grün**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "Category=InteropFreeSwitch"`
Expected: alle grün gegen echtes FreeSWITCH (Register + Media + DTMF + Hold + Transfer + Short-Soak + lokal-SDES-Content).

- [ ] **Step 2: Asterisk-Suite + PR-CI-Gate unberührt**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 --filter "Category=Interop&Category!=InteropLocalMedia&Category!=InteropFreeSwitch"`
Expected: die Asterisk-Interop-Tests wie zuvor grün (die genau dem PR-CI-Gate entsprechen).

- [ ] **Step 3: `src/` unberührt**

Run: `git diff --stat b799b6d..HEAD -- src/` (b799b6d = B.1-Tip, Basis des B.2-Branches)
Expected: leer.

---

## Abschluss

Nach Task 7: dieselbe Zwei-Bein-Media-Szenario-Matrix läuft gegen **zwei** Fremd-Stacks (Asterisk im CI-Gate, FreeSWITCH lokal-first) — die `IPbxFixture`-Abstraktion aus B.1 ist bewiesen. Branch per `superpowers:finishing-a-development-branch` abschließen (PR-Erstellung durch den User). **Folge-Schritt (separat):** FreeSWITCH ins PR-CI-Gate aufnehmen, sobald über mehrere Läufe stabil (Kategorie umhängen + evtl. FreeSWITCH-Image-Pull-Step in der CI).
