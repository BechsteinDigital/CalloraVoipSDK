# Spec: Zwei-Bein-Media-Interop gegen Asterisk (bidirektional gemessen)

**Status:** Entwurf zur Freigabe · **Datum:** 2026-07-23 · **Branch:** `feat/two-leg-media-interop` (Worktree `.claude/worktrees/feat+interop-soak-audit`) · **Teil von:** Interop-+Soak-+Audit-Paket (Phase 4.2 — Call + echte Media)

## 1. Kontext & Ziel

Die Asterisk-Interop-Matrix misst RTP-Media heute nur **unidirektional**: der SDK nutzt `SilenceAudioDevice` (sendet nichts), Asterisk spielt `Milliwatt()`, der SDK empfängt, Assertion = `ICall.RtpStatistics.PacketsReceived > 0` (`AsteriskCallHappyPathInteropTests.cs:64`). Es gibt keinen Test, in dem der SDK **aktiv Audio sendet** und ein zweites Bein den Fluss end-to-end **misst**.

**Ziel:** Ein echter Zwei-Bein-Media-Call — zwei `VoipClient`-Instanzen telefonieren über den echten Asterisk-PBX miteinander (A → Asterisk brückt → B) — mit **bidirektionaler, quantifizierter Messung** des Medienpfads auf drei Ebenen: RTP-Paketzähler, RTCP-Qualitätsmetriken, byte-exakte Inhaltsverifikation.

**Nicht-Ziel:** SDK-Änderungen. Reine Testinfrastruktur + Doku (stehende Projektgrenze). Ergebnisse fließen ins Register `docs/audit/INTEROP_SOAK_AUDIT.md`.

## 2. Ist-Zustand — verifizierte öffentliche APIs

Der Zwei-Bein-Fall ist mit vorhandener öffentlicher API erreichbar; es fehlt nur die Harness-/Test-Verdrahtung.

- **Senden:** `IVoipClient.Media` (`IMediaManager`, `IVoipClient.cs:24`) → `CreateSender()` (`IMediaManager.cs:28`) → `IMediaSender.AttachToCall(ICall)` + `SendAsync(MediaFrame, ct)` (`IMediaSender.cs`, → `call.SendAudioFrameAsync`, `MediaSender.cs:77`).
- **Empfangen:** `IMediaManager.CreateReceiver()` (`IMediaManager.cs:23`) → `IMediaReceiver.AttachToCall(ICall)` + `FrameReceived` (`MediaFrameReceivedEventArgs.Frame`, `IMediaReceiver.cs:18/23`).
- **Frame-Typ:** `MediaFrame(byte[] Payload, int PayloadType, uint DurationRtpUnits)` (belegt in `CallMediaTapContractTests.cs:119`).
- **Inbound annehmen:** `IVoipClient.IncomingCall` → `e.Call` → `call.AcceptAsync()` (`AsteriskInboundCallInteropTests.cs:34/54`).
- **Metriken je Leg auf `ICall`:**
  - `RtpStatistics` (`CallRtpStatistics`): `PacketsSent`, `PacketsReceived`, `PacketsExpected`, `CumulativePacketsLost`, `FractionLost`, `InterarrivalJitterRtpUnits`, `LocalSsrc`, `RemoteSsrc`.
  - `QualitySnapshot` (`CallQualitySnapshot`): `RtcpActive`, `LocalReceiveJitterMs`, `LocalReceivePacketLossPercent`, `RemoteReportJitterMs?`, `RemoteReportPacketLossPercent?`, `RoundTripTimeMs?`, `RtcpPacketsSent/Received`, `RemoteMosListeningQuality?`, `RemoteMosConversationalQuality?` + Event `QualitySnapshotChanged`.
  - `MediaParameters` (`CallMediaParameters`): `PayloadType`, `CodecName`, `ClockRate`.

## 3. Ziel-Topologie

```
VoipClient A (Endpoint 6001, Host)                VoipClient B (Endpoint 6003, Host)
      │  DialAndWaitUntilConnectedAsync("6003")           ▲  IncomingCall → AcceptAsync
      ▼                                                   │
   INVITE ──► Asterisk (Container)  ── Dial(PJSIP/6003) ──┘
      RTP A↔Asterisk  ◄──── Core-Bridge (Passthrough) ────►  RTP B↔Asterisk
```

Zwei unabhängige host↔container-RTP-Sessions, im Asterisk-Core gebrückt. Der Happy-Path beweist host↔container-RTP für ein Bein bereits — zwei Beine sind zwei davon (empirisch zu bestätigen, nicht anzunehmen).

## 4. Komponenten

Muster wie das bestehende `RtpMediaLoopback`: Fixtures in `CalloraVoipSdk.InteropHarness`, Tests in `CalloraVoipSdk.InteropTests` asserten.

1. **`TwoLegBridgedCall`** (neu, InteropHarness) — kapselt: Erzeugung + Registrierung beider Clients, A wählt die Bridge-Extension, B nimmt inbound an (`IncomingCall` → `AcceptAsync`), hängt beidseitig `IMediaSender` + `IMediaReceiver` an, startet die markierten Sende-Ströme, exponiert Mess-Accessoren pro Leg (`ICall` A/B, gesammelte Empfangs-Frames B/A, letzte `QualitySnapshot`s). `IAsyncDisposable` (Hangup + Client-Dispose, Reihenfolge deterministisch).
2. **`MarkedPcmuSource`** (neu, InteropHarness) — erzeugt 20-ms-PCMU-`MediaFrame`s (PT 0, 160 Samples) mit eingebettetem Sequenz-Zähler in den ersten Payload-Bytes; monotone Zählung je Strom. Nötig, weil `SilenceAudioDevice` nichts sendet.
3. **`AsteriskContainer`-Erweiterung** (InteropTests-Fixture) — zweiter **Plain-RTP**-Endpunkt `6003` (analog `6001`, kein `media_encryption`) + Dialplan `exten => 6003,1,Dial(PJSIP/6003,30)`. Bewusst plain, weil Inhaltsverifikation Same-Codec-Passthrough ohne SRTP-Overhead braucht; SRTP-Variante ist optionales Folge-Slice.
4. **`AsteriskTwoLegMediaInteropTests`** (neu, InteropTests) — treibt die Fixture, asserted die drei Ebenen. `[Trait("Category","Interop")]`, `[DockerRequiredFact]`.

## 5. Messebenen (alle drei, geschichtet)

- **Ebene 1 — RTP-Paketzähler (Basis, hart):** auf A **und** B `RtpStatistics.PacketsSent > 0 && PacketsReceived > 0` (Deadline-Polling wie Happy-Path). Beweist Media in beide Richtungen durch den PBX.
- **Ebene 2 — RTCP-Qualität (hart, sofern befüllt):** sobald `QualitySnapshot.RtcpActive` auf beiden Legs: prüfe plausible `LocalReceiveJitterMs` (endlich, ≥0), `LocalReceivePacketLossPercent` (0–100), und — soweit Asterisk RTCP RR/XR liefert — `RemoteReportJitterMs`/`RemoteReportPacketLossPercent`/`RoundTripTimeMs`/MOS nicht-null. Validiert, dass der SDK Asterisks echtes RTCP parst. **Falls `RemoteReport*`/MOS leer bleiben → Register-Fund (Coupling/Interop), Assertion per SPLIT+SKIP entschärft.**
- **Ebene 3 — Inhalt byte-exakt:** A sendet N markierte Frames; B sammelt via `FrameReceived`; verifiziere einen zusammenhängenden Sequenz-Lauf (Rand-Loss toleriert, z. B. ≥ 80 % einer kontiguierlichen Strecke). Voraussetzung: PCMU↔PCMU-Passthrough (beide Legs `MediaParameters.PayloadType == 0`). Optional symmetrisch B→A.

## 6. Fehlerbehandlung & Fallbacks (measure-first)

- **Passthrough-Fragilität:** repacketisiert Asterisk oder verschiebt Frame-Grenzen → Ebene 3 wird `[Fact(Skip="Fxxx — siehe Register")]`; Ebenen 1+2 bleiben hart. (Projekt-Policy SPLIT+SKIP.)
- **QualitySnapshot-Treue (Bezug F004):** RTT/RTCP war an L2 nur ein statischer Hint; an L4/`VoipClient` ist der RTCP-Quality-Monitor via `CallMediaOrchestrator` verdrahtet — zu bestätigen. Leere Felder = Fund, kein Fix.
- **Networking:** zwei host↔container-RTP-Beine empirisch bestätigen; Linux/CI + Container-Bridge-IP wie in der bestehenden Fixture (macOS/Windows-Docker-Desktop out of scope).
- **Timing:** Deadline-Polling + Playout-Anlauf tolerieren (wie Happy-Path); markierte Ströme mit 20-ms-Kadenz über eine feste Sendedauer.
- **Scope-Ehrlichkeit (Register):** Audio wird SDK-seitig via `IMediaSender` injiziert (kein Mikrofon/Codec-Encode) — Transport-/Pfad-Messung, keine akustische Qualität. Explizit so benennen.

## 7. Scope & Nicht-Ziele

**Drin:** Ein Kern-Test (bidirektionaler Bridged-Call, Ebenen 1–3), Plain RTP, PCMU-Pinning, beide Legs senden. **Draußen (optionale Folge-Slices):** SRTP-SDES-Variante (Endpoint 6002), Codec-Mismatch/Transcoding-Fall, Video, FreeSWITCH/andere Peers. **Keine SDK-Änderungen.**

## 8. Testing & CI

`[Trait("Category","Interop")]` → läuft im Ubuntu-Interop-Job, nicht im PR-Schnellpfad (`ci.yml`-Filter). `[DockerRequiredFact]`-Gate. Kein Soak (kurzer, deterministischer Call). Bestehende Fixture-Netzwerkannahmen (Linux, Bridge-IP) gelten.

## 9. Entscheidungen

- `// DECISION:` Senden via `IMediaSender` (öffentliche Tap-API), nicht via neues Test-`IAudioDevice` — näher am Consumer-Pfad, bereits vertragsgetestet.
- `// DECISION:` Zweiter Plain-RTP-Endpunkt `6003` + `Dial`-Bridge (statt `6001` doppelt zu belegen) — eindeutiges Routing.
- `// DECISION:` PCMU-Pinning für byte-exakten Passthrough.
- `// DECISION:` Beide Legs senden (bidirektionale Zähler); Inhaltsverifikation primär A→B, B→A optional.
- `// DECISION:` Ebene 3 und leere Ebene-2-Felder unterliegen SPLIT+SKIP statt Testabbruch.

## 10. Register-Bezug & mögliche Funde

Neues Register-Kapitel „Zwei-Bein-Media (bidirektional)". Erwartbare Fund-Kandidaten: (a) `RemoteReport*`/MOS gegen Asterisk leer (RTCP-XR-Parsing/Coupling); (b) Passthrough-Marker-Verschiebung; (c) unerwartete Loss/Jitter-Charakteristik. Jeder Fund = Doku + Skip, kein Fix.

## 11. Slice-Skizze (Übergabe an writing-plans)

1. `MarkedPcmuSource` + Unit-Test (Marker-Roundtrip in-memory).
2. Asterisk-Fixture: Endpunkt `6003` + Bridge-Extension (+ Smoke: B registriert, A→B klingelt/verbindet).
3. `TwoLegBridgedCall`-Fixture (Aufbau/Accept/Sender+Receiver/Dispose).
4. Interop-Test Ebene 1 (bidirektionale Paketzähler) — hart.
5. Interop-Test Ebene 2 (RTCP-Qualität) — hart, mit SPLIT+SKIP-Gate für leere Remote-Felder.
6. Interop-Test Ebene 3 (byte-exakter Inhalt A→B) — mit SPLIT+SKIP-Gate.
7. Register-Update + Coverage-Notiz.
