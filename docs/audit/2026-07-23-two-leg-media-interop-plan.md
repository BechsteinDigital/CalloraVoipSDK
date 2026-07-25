# Zwei-Bein-Media-Interop gegen Asterisk — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Zwei `VoipClient`-Legs telefonieren über einen echten Asterisk-PBX miteinander, mit bidirektional gemessenem Medienpfad (RTP-Paketzähler, RTCP-Qualität, byte-exakte Inhaltsverifikation).

**Architecture:** Client-A (Endpoint 6001) wählt Extension `6003`; Asterisk brückt via `Dial(PJSIP/6003)` an Client-B (Endpoint 6003), der inbound annimmt. Beide Legs senden markiertes PCMU via `IMediaSender` und empfangen via `IMediaReceiver`; Metriken werden auf `ICall.RtpStatistics`/`QualitySnapshot` gelesen. Reine Testinfrastruktur, keine SDK-Änderungen; Funde → Register (SPLIT+SKIP-Policy).

**Tech Stack:** .NET 8/9/10, xUnit 2.4.2, Testcontainers 4.13.0 (`andrius/asterisk:22`), `[DockerRequiredFact]`-Gate. Docker lokal verfügbar → **measure-first**: erst gegen echten Asterisk messen, dann Assertions/Skips festschreiben.

**Design-Spec:** `docs/audit/2026-07-23-two-leg-media-interop-design.md`

**Dateiplatzierung (Planungsbefund):** Die neuen Fixtures leben in **`CalloraVoipSdk.InteropTests`** (referenziert Client+Core), NICHT in `InteropHarness` (bewusst Core-only, kein Facade). Das hält den Harness schlank.

**Verifizierte Signaturen (alle Tasks nutzen diese):**
- `MediaFrame(ReadOnlyMemory<byte> Payload, int PayloadType, uint DurationRtpUnits)` — `CalloraVoipSdk.Core.Application.Media`.
- `IVoipClient.Media` → `IMediaManager.CreateSender()`/`CreateReceiver()`.
- `IMediaSender.AttachToCall(ICall)`, `SendAsync(MediaFrame, ct)`, `Detach()`, `IDisposable` — Frames werden verworfen, solange der Call nicht `Connected`/`OnHold` ist.
- `IMediaReceiver.AttachToCall(ICall)`, `FrameReceived` (`MediaFrameReceivedEventArgs.Frame` → `MediaFrame`), `Detach()`, `IDisposable`.
- `client.ConnectAsync(SipAccount, ConnectOptions)` → `ConnectResult { bool IsSuccess, ConnectStatus Status, IPhoneLine? Line }`.
- `client.DialAndWaitUntilConnectedAsync(IPhoneLine, string uri, DialWaitOptions)` → `DialResult { bool IsSuccess, DialStatus Status, ICall? Call }`.
- `client.IncomingCall` (`EventHandler<IncomingCallEventArgs>`, `e.Call` = `ICall`) → `call.AcceptAsync(ct=default)`.
- `ICall`: `State` (`CallState`), `MediaParameters` (`CallMediaParameters { int PayloadType }`), `RtpStatistics` (`CallRtpStatistics?`: `PacketsSent`, `PacketsReceived`, `CumulativePacketsLost`, `FractionLost`, `InterarrivalJitterRtpUnits`), `QualitySnapshot` (`CallQualitySnapshot`: `RtcpActive`, `LocalReceiveJitterMs`, `LocalReceivePacketLossPercent`, `RemoteReportJitterMs?`, `RemoteReportPacketLossPercent?`, `RoundTripTimeMs?`, `RemoteMosListeningQuality?`), `HangupAsync()`.
- `SipAccount { string SipServer, int Port, string Username, string Password, SipTransport Transport }` (`Core.Domain.Lines`; `SipTransport` per Alias, s. Happy-Path-Test).
- `AsteriskContainer`: `StartAsync()`, `ContainerIpAddress`, `Username`/`Password` (=6001/secret), `CallTargetUri(ext, port=5060)`, `IAsyncDisposable`.

---

### Task 1: `MarkedPcmuSource` — markierte PCMU-Frames

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/Media/MarkedPcmuSource.cs`
- Test: `tests/CalloraVoipSdk.InteropTests/Media/MarkedPcmuSourceTests.cs`

- [ ] **Step 1: Failing Test schreiben**

```csharp
using CalloraVoipSdk.InteropTests.Media;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Media;

public sealed class MarkedPcmuSourceTests
{
    [Fact]
    public void Next_produces_monotonic_readable_sequence_markers()
    {
        var src = new MarkedPcmuSource();
        var f0 = src.Next();
        var f1 = src.Next();

        Assert.Equal(0u, MarkedPcmuSource.ReadSequence(f0.Payload.Span));
        Assert.Equal(1u, MarkedPcmuSource.ReadSequence(f1.Payload.Span));
        Assert.Equal(MarkedPcmuSource.FrameBytes, f0.Payload.Length);
        Assert.Equal(0, f0.PayloadType);          // PCMU
        Assert.Equal(160u, f0.DurationRtpUnits);  // 20 ms @ 8 kHz
    }
}
```

- [ ] **Step 2: Test rot verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~MarkedPcmuSourceTests"`
Expected: FAIL — `MarkedPcmuSource` existiert nicht (Kompilerfehler).

- [ ] **Step 3: Minimale Implementierung**

```csharp
using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media;

namespace CalloraVoipSdk.InteropTests.Media;

/// <summary>
/// Erzeugt fortlaufend markierte 20-ms-PCMU-<see cref="MediaFrame"/>s (PT 0, 160 Bytes). Die ersten
/// 4 Payload-Bytes tragen einen monoton steigenden uint32-Sequenzzähler (Big-Endian); der Rest ist
/// PCMU-Stille (0xFF). Empfangsseitig lässt sich daraus die gesendete Sequenz rekonstruieren.
/// </summary>
public sealed class MarkedPcmuSource
{
    public const int PayloadType = 0;
    public const int FrameBytes = 160;
    public const uint DurationRtpUnits = 160;

    private uint _next;

    /// <summary>Nächster markierter Frame; der Sequenzzähler beginnt bei 0 und steigt je Aufruf um 1.</summary>
    public MediaFrame Next()
    {
        var payload = new byte[FrameBytes];
        BinaryPrimitives.WriteUInt32BigEndian(payload, _next++);
        payload.AsSpan(4).Fill(0xFF);
        return new MediaFrame(payload, PayloadType, DurationRtpUnits);
    }

    /// <summary>Liest den Sequenzmarker aus einem empfangenen Payload (≥ 4 Bytes).</summary>
    public static uint ReadSequence(ReadOnlySpan<byte> payload) =>
        BinaryPrimitives.ReadUInt32BigEndian(payload);
}
```

- [ ] **Step 4: Test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~MarkedPcmuSourceTests"`
Expected: PASS (1 Test).

- [ ] **Step 5: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Media/MarkedPcmuSource.cs tests/CalloraVoipSdk.InteropTests/Media/MarkedPcmuSourceTests.cs
git commit -m "test(interop): MarkedPcmuSource — markierte PCMU-Frames für Zwei-Bein-Inhaltsverifikation"
```

---

### Task 2: Asterisk-Fixture — Endpoint 6003 + Bridge-Extension

**Files:**
- Modify: `tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainer.cs` (`PjsipConf`, `ExtensionsConf`, neue Accessoren)
- Test: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs`

- [ ] **Step 1: Failing Test schreiben** (Endpoint 6003 registrierbar)

```csharp
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegMediaInteropTests
{
    private static VoipClient NewClient() =>
        new(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0", SrtpPolicy = SrtpPolicy.Disabled });

    [DockerRequiredFact]
    public async Task SecondPlainRtpEndpoint_6003_Registers()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();

        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = asterisk.BridgeUsername,
                Password = asterisk.BridgePassword,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.True(reg.IsSuccess, $"Registrierung 6003 fehlgeschlagen: Status={reg.Status}");
    }
}
```

- [ ] **Step 2: Test rot verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~SecondPlainRtpEndpoint_6003_Registers"`
Expected: FAIL — `asterisk.BridgeUsername`/`BridgePassword` existieren nicht (Kompilerfehler).

- [ ] **Step 3: Fixture erweitern**

In `AsteriskContainer.cs`, im `PjsipConf`-String NACH dem `[6002]`-`type=aor`-Block (vor dem schließenden `;`) diesen Endpoint anfügen (PCMU-only → garantierter Same-Codec-Passthrough):

```csharp
        "\n" +
        // Dritter Endpoint: Plain RTP, PCMU-only. Ziel der Zwei-Bein-Bridge — PCMU auf beiden Legs
        // garantiert Same-Codec-Passthrough für die byte-exakte Inhaltsverifikation.
        "[6003]\n" +
        "type=endpoint\n" +
        "context=default\n" +
        "disallow=all\n" +
        "allow=ulaw\n" +
        "auth=6003\n" +
        "aors=6003\n" +
        "\n" +
        "[6003]\n" +
        "type=auth\n" +
        "auth_type=userpass\n" +
        "username=6003\n" +
        "password=secret\n" +
        "\n" +
        "[6003]\n" +
        "type=aor\n" +
        "max_contacts=1\n";
```

(Das bisherige `[6002]`-`max_contacts=1\n";` wird zu `max_contacts=1\n" +` — das schließende `;` wandert ans Ende des neuen Blocks.)

Im `ExtensionsConf`-String die Bridge-Extension anfügen (vor dem schließenden `;` der letzten Zeile `same => n,Milliwatt()\n"`) — die Zeile wird zu `...Milliwatt()\n" +` und danach:

```csharp
        "exten => 6003,1,Dial(PJSIP/6003,30)\n";   // brückt den Anruf an den zweiten registrierten SDK-Endpoint
```

Neue Accessoren nach `SdesPassword`:

```csharp
    /// <summary>Benutzername des dritten Plain-RTP-Endpoints (PCMU-only), Ziel der Zwei-Bein-Bridge.</summary>
    public string BridgeUsername => "6003";

    /// <summary>Passwort des Bridge-Endpoints (Digest-Auth).</summary>
    public string BridgePassword => "secret";
```

- [ ] **Step 4: Test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~SecondPlainRtpEndpoint_6003_Registers"`
Expected: PASS. Falls FAIL: `asterisk.GetConsoleLogsAsync()` prüfen — häufig ein pjsip.conf-Syntaxfehler (führendes Leerzeichen, fehlende Leerzeile zwischen Sektionen).

- [ ] **Step 5: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainer.cs tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs
git commit -m "test(interop): Asterisk-Fixture um Plain-RTP-Endpoint 6003 + Bridge-Extension erweitert"
```

---

### Task 3: `TwoLegBridgedCall`-Fixture — Aufbau, Accept, Media, Teardown

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/Media/TwoLegBridgedCall.cs`
- Test: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs` (neuer Test)

- [ ] **Step 1: Failing Test schreiben** (Bridged-Call etabliert beide Legs)

Im Test-Class-Body ergänzen:

```csharp
    [DockerRequiredFact]
    public async Task BridgedCall_ConnectsBothLegs()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        Assert.Equal(CalloraVoipSdk.Core.Domain.Calls.CallState.Connected, bridged.CallerCall.State);
        Assert.Equal(CalloraVoipSdk.Core.Domain.Calls.CallState.Connected, bridged.CalleeCall.State);
        Assert.Equal(0, bridged.CallerCall.MediaParameters!.PayloadType);  // PCMU beidseitig
        Assert.Equal(0, bridged.CalleeCall.MediaParameters!.PayloadType);
    }
```

- [ ] **Step 2: Test rot verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~BridgedCall_ConnectsBothLegs"`
Expected: FAIL — `TwoLegBridgedCall` existiert nicht.

- [ ] **Step 3: Fixture implementieren**

```csharp
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Media;

/// <summary>Ergebnis eines bidirektionalen Media-Laufs: die je Seite empfangenen Sequenzmarker.</summary>
public sealed record TwoLegMediaResult(
    IReadOnlyList<uint> CalleeReceivedSequences,
    IReadOnlyList<uint> CallerReceivedSequences);

/// <summary>
/// L4-Fixture: zwei <see cref="VoipClient"/>-Legs über einen echten Asterisk gebrückt (A=6001 wählt
/// Extension 6003 → Asterisk Dial(PJSIP/6003) → B=6003 nimmt inbound an). Kapselt Aufbau, beidseitige
/// Media-Injektion via <see cref="IMediaSender"/> und -Erfassung via <see cref="IMediaReceiver"/>.
/// </summary>
public sealed class TwoLegBridgedCall : IAsyncDisposable
{
    private readonly VoipClient _callerClient;
    private readonly VoipClient _calleeClient;

    public ICall CallerCall { get; }   // A (6001)
    public ICall CalleeCall { get; }   // B (6003)

    private TwoLegBridgedCall(VoipClient callerClient, VoipClient calleeClient, ICall callerCall, ICall calleeCall)
    {
        _callerClient = callerClient;
        _calleeClient = calleeClient;
        CallerCall = callerCall;
        CalleeCall = calleeCall;
    }

    private static VoipClient NewClient() =>
        new(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0", SrtpPolicy = SrtpPolicy.Disabled });

    private static async Task<IPhoneLine> RegisterAsync(AsteriskContainer asterisk, VoipClient client, string user, string pass)
    {
        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = user,
                Password = pass,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        if (!reg.IsSuccess)
            throw new InvalidOperationException($"Registrierung {user} fehlgeschlagen: {reg.Status}");
        return reg.Line!;
    }

    /// <summary>Baut den gebrückten Call auf und wartet, bis beide Legs Connected sind.</summary>
    public static async Task<TwoLegBridgedCall> StartAsync(AsteriskContainer asterisk)
    {
        var callerClient = NewClient();
        var calleeClient = NewClient();
        try
        {
            var callerLine = await RegisterAsync(asterisk, callerClient, asterisk.Username, asterisk.Password);
            await RegisterAsync(asterisk, calleeClient, asterisk.BridgeUsername, asterisk.BridgePassword);

            // B nimmt den eingehenden (von Asterisk gebrückten) Call an. Der Handler erfasst nur den Call;
            // A's Dial blockiert bis zum Accept → beides läuft nebenläufig.
            var calleeTcs = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
            calleeClient.IncomingCall += (_, e) => calleeTcs.TrySetResult(e.Call);

            var dialTask = callerClient.DialAndWaitUntilConnectedAsync(
                callerLine, asterisk.CallTargetUri("6003"),
                new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });

            var calleeCall = await calleeTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await calleeCall.AcceptAsync();

            var dial = await dialTask;
            if (!dial.IsSuccess)
                throw new InvalidOperationException($"Bridged-Dial fehlgeschlagen: {dial.Status}");

            return new TwoLegBridgedCall(callerClient, calleeClient, dial.Call!, calleeCall);
        }
        catch
        {
            callerClient.Dispose();
            calleeClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sendet <paramref name="duration"/> lang markierte PCMU-Frames in BEIDE Richtungen (Kadenz
    /// <paramref name="frameInterval"/>, Default 20 ms) und sammelt die je Seite empfangenen Marker.
    /// Läuft lang genug, dass RTCP-Reports die Metriken befüllen (Default 8 s).
    /// </summary>
    public async Task<TwoLegMediaResult> RunBidirectionalMediaAsync(
        TimeSpan? duration = null, TimeSpan? frameInterval = null)
    {
        var runFor = duration ?? TimeSpan.FromSeconds(8);
        var interval = frameInterval ?? TimeSpan.FromMilliseconds(20);

        var calleeSeq = new List<uint>();
        var callerSeq = new List<uint>();
        var gate = new object();

        using var recvAtCallee = _calleeClient.Media.CreateReceiver();
        using var recvAtCaller = _callerClient.Media.CreateReceiver();
        recvAtCallee.FrameReceived += (_, e) => { lock (gate) calleeSeq.Add(MarkedPcmuSource.ReadSequence(e.Frame.Payload.Span)); };
        recvAtCaller.FrameReceived += (_, e) => { lock (gate) callerSeq.Add(MarkedPcmuSource.ReadSequence(e.Frame.Payload.Span)); };
        recvAtCallee.AttachToCall(CalleeCall);
        recvAtCaller.AttachToCall(CallerCall);

        using var sendFromCaller = _callerClient.Media.CreateSender();
        using var sendFromCallee = _calleeClient.Media.CreateSender();
        sendFromCaller.AttachToCall(CallerCall);
        sendFromCallee.AttachToCall(CalleeCall);

        var srcA = new MarkedPcmuSource();
        var srcB = new MarkedPcmuSource();
        using var cts = new CancellationTokenSource(runFor);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await sendFromCaller.SendAsync(srcA.Next(), cts.Token);
                await sendFromCallee.SendAsync(srcB.Next(), cts.Token);
                await Task.Delay(interval, cts.Token);
            }
        }
        catch (OperationCanceledException) { /* Ende der Laufdauer */ }

        lock (gate)
            return new TwoLegMediaResult(calleeSeq.ToArray(), callerSeq.ToArray());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await CallerCall.HangupAsync(); } catch { /* best effort */ }
        try { await CalleeCall.HangupAsync(); } catch { /* best effort */ }
        _callerClient.Dispose();
        _calleeClient.Dispose();
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Test grün verifizieren (measure-first)**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~BridgedCall_ConnectsBothLegs"`
Expected: PASS. Falls die PayloadType-Assertion fehlschlägt (SDK verhandelte nicht PCMU/PT 0): via `asterisk.GetConsoleLogsAsync()` das ausgehandelte SDP prüfen und die Codec-Präferenz des Callers auf PCMU pinnen (`VoipConfiguration` / Codec-Präferenz). Falls das Timing klemmt (Accept vs. Dial): Reihenfolge in `StartAsync` prüfen — B muss VOR dem Await auf `dialTask` annehmen.

- [ ] **Step 5: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Media/TwoLegBridgedCall.cs tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs
git commit -m "test(interop): TwoLegBridgedCall-Fixture — gebrückter Zwei-Bein-Call über Asterisk"
```

---

### Task 4: Ebene 1 — bidirektionale RTP-Paketzähler (hart)

**Files:**
- Test: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs` (neuer Test)

- [ ] **Step 1: Failing Test schreiben**

```csharp
    [DockerRequiredFact]
    public async Task BridgedCall_FlowsRtpInBothDirections()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        await bridged.RunBidirectionalMediaAsync();

        AssertBidirectionalRtp(bridged.CallerCall, "Caller");
        AssertBidirectionalRtp(bridged.CalleeCall, "Callee");

        static void AssertBidirectionalRtp(CalloraVoipSdk.Core.Domain.Calls.ICall call, string label)
        {
            var rtp = call.RtpStatistics;
            Assert.True(rtp is { PacketsSent: > 0 }, $"{label}: keine gesendeten RTP-Pakete.");
            Assert.True(rtp is { PacketsReceived: > 0 }, $"{label}: keine empfangenen RTP-Pakete.");
        }
    }
```

- [ ] **Step 2: Test rot/grün empirisch messen**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~BridgedCall_FlowsRtpInBothDirections"`
Expected: PASS, sobald Media in beide Richtungen fließt. **Measure-first:** Falls `RtpStatistics` null/0 bleibt, Laufdauer erhöhen (RTCP-Report-Intervall ~5 s → ggf. `RunBidirectionalMediaAsync(TimeSpan.FromSeconds(12))`) und Container-Netzwerk bestätigen (beide host↔container-RTP-Beine). Erst wenn beide Richtungen stabil messbar sind, gilt der Test als grün.

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs
git commit -m "test(interop): Ebene 1 — bidirektionale RTP-Paketzähler im gebrückten Zwei-Bein-Call"
```

---

### Task 5: Ebene 2 — RTCP-Qualitätsmetriken (measure-first, SPLIT+SKIP für Remote-Felder)

**Files:**
- Test: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs` (neuer Test)
- Ggf. Modify: `docs/audit/INTEROP_SOAK_AUDIT.md` (neuer Fund, falls Remote-Felder leer)

- [ ] **Step 1: Test schreiben — lokale RTCP-Metriken hart**

```csharp
    [DockerRequiredFact]
    public async Task BridgedCall_PopulatesLocalRtcpQuality()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(10));

        AssertLocalQuality(bridged.CallerCall, "Caller");
        AssertLocalQuality(bridged.CalleeCall, "Callee");

        static void AssertLocalQuality(CalloraVoipSdk.Core.Domain.Calls.ICall call, string label)
        {
            var q = call.QualitySnapshot;
            Assert.True(q.RtcpActive, $"{label}: RTCP nicht aktiv.");
            Assert.True(double.IsFinite(q.LocalReceiveJitterMs) && q.LocalReceiveJitterMs >= 0,
                $"{label}: implausibler Jitter {q.LocalReceiveJitterMs}.");
            Assert.InRange(q.LocalReceivePacketLossPercent, 0.0, 100.0);
        }
    }
```

- [ ] **Step 2: Messen — Verhalten der Remote-/MOS-Felder feststellen**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~BridgedCall_PopulatesLocalRtcpQuality"`
Expected: PASS für die lokalen Felder (`RtcpActive`, Jitter, Loss). **Measure-first — jetzt beobachten:** In einem Ad-hoc-Durchlauf zusätzlich `RemoteReportJitterMs`, `RemoteReportPacketLossPercent`, `RoundTripTimeMs`, `RemoteMosListeningQuality` protokollieren (temporär via `Console.WriteLine` oder Debugger). Entscheidung:
  - **Wenn Remote-Felder gegen Asterisk befüllt sind:** einen zweiten harten Test `BridgedCall_PopulatesRemoteRtcpReport` hinzufügen, der `RemoteReport*`/`RoundTripTimeMs` nicht-null asserted. Kein Skip.
  - **Wenn sie leer bleiben:** diesen zweiten Test als `[DockerRequiredFact(Skip = "Fxxx — Remote-RTCP/MOS gegen Asterisk leer, siehe Register")]` anlegen und den Fund im Register dokumentieren (Coupling/Interop, kein Fix). `Fxxx` = nächste freie Fund-Nummer im Register.

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs docs/audit/INTEROP_SOAK_AUDIT.md
git commit -m "test(interop): Ebene 2 — lokale RTCP-Qualität hart; Remote-Report measure-first (SPLIT+SKIP)"
```

---

### Task 6: Ebene 3 — byte-exakte Inhaltsverifikation (measure-first, SPLIT+SKIP)

**Files:**
- Test: `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs` (neuer Test)
- Ggf. Modify: `docs/audit/INTEROP_SOAK_AUDIT.md`

- [ ] **Step 1: Test schreiben — kontiguierlicher Sequenz-Lauf A→B**

```csharp
    [DockerRequiredFact]
    public async Task BridgedCall_DeliversMarkedContentEndToEnd()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        var result = await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(8));

        var received = result.CalleeReceivedSequences;
        Assert.NotEmpty(received);
        // Größter kontiguierlicher Lauf empfangener Marker; Rand-/Playout-Verluste toleriert.
        var longestRun = LongestContiguousRun(received);
        Assert.True(longestRun >= 50,
            $"Nur {longestRun} zusammenhängende markierte Frames end-to-end (von {received.Count} empfangen).");

        static int LongestContiguousRun(IReadOnlyList<uint> seqs)
        {
            var set = new HashSet<uint>(seqs);
            var best = 0;
            foreach (var s in set)
            {
                if (set.Contains(s - 1)) continue; // nur Lauf-Anfänge
                var len = 1;
                while (set.Contains(s + (uint)len)) len++;
                best = Math.Max(best, len);
            }
            return best;
        }
    }
```

- [ ] **Step 2: Messen — Passthrough-Verhalten feststellen**

Run: `dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "FullyQualifiedName~BridgedCall_DeliversMarkedContentEndToEnd"`
Expected: PASS, wenn Asterisk PCMU byte-exakt durchreicht (Marker überleben). **Measure-first:** Falls die empfangenen Marker Unsinn sind (Asterisk transcodiert/repacketisiert → Payload verändert), den Test in `[DockerRequiredFact(Skip = "Fxxx — Asterisk-Passthrough verändert Payload/Marker, siehe Register")]` umwandeln und den Fund im Register dokumentieren. Die Schwelle `>= 50` ggf. an die empirisch stabile kontiguierliche Strecke anpassen (dokumentieren, nicht raten).

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.InteropTests/Calls/AsteriskTwoLegMediaInteropTests.cs docs/audit/INTEROP_SOAK_AUDIT.md
git commit -m "test(interop): Ebene 3 — byte-exakte Inhaltsverifikation A→B (measure-first, SPLIT+SKIP)"
```

---

### Task 7: Register-Update + Coverage-Notiz

**Files:**
- Modify: `docs/audit/INTEROP_SOAK_AUDIT.md`

- [ ] **Step 1: Register-Kapitel ergänzen**

Neues Kapitel „Zwei-Bein-Media (bidirektional gemessen)" mit: Topologie (A=6001 → Extension 6003 → B=6003, Plain RTP/PCMU-Passthrough), abgedeckte Ebenen (1 Paketzähler hart, 2 lokale RTCP hart / Remote je nach Messung, 3 Inhalt je nach Passthrough), plus die in Tasks 5/6 ggf. gefundenen `Fxxx`-Einträge. **Coverage-Ehrlichkeit** explizit notieren: Audio wird SDK-seitig via `IMediaSender` injiziert (kein Mikrofon/Codec-Encode) — Transport-/Pfad-Messung, keine akustische Qualität; nur Plain RTP/PCMU; SRTP-Variante und Codec-Mismatch offen.

- [ ] **Step 2: Verifizieren, dass das Register konsistent ist** (keine widersprüchlichen Fund-Nummern, Skips ↔ Register-Einträge decken sich).

- [ ] **Step 3: Commit**

```bash
git add docs/audit/INTEROP_SOAK_AUDIT.md
git commit -m "docs(audit): Register — Zwei-Bein-Media-Interop-Coverage + etwaige Funde"
```

---

## Abschluss

Nach Task 7: volle InteropTests-Suite gegen echten Asterisk laufen lassen —
`dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0` — und bestätigen, dass die neuen Zwei-Bein-Tests grün (bzw. dokumentiert-geskippt) sind und keine bestehenden Interop-Tests regressieren. Danach den Branch `feat/two-leg-media-interop` per `superpowers:finishing-a-development-branch` abschließen (PR-Erstellung durch den User, gh-CLI-Hook-Beschränkung).
