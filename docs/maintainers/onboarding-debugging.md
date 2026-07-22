# Onboarding & Debugging

Der geführte Einstieg für neue Maintainer plus die Diagnose-Werkzeuge des Repos.
Voraussetzung: [`MAINTAINING.md`](../../MAINTAINING.md) gelesen (Architektur-Landkarte,
Arbeitsabläufe). Domänen-Grundlagen (SIP/SDP/RTP/ICE) setzt dieses Repo voraus — die
Code-Kommentare zitieren die RFCs, sie erklären sie nicht. Sinnvolle Lese-Reihenfolge für
Neulinge ohne VoIP-Hintergrund: RFC 3261 §4 (Overview) → RFC 3264 → RFC 3550 §1–§6 →
RFC 8445 §2 (Overview) — danach tragen die Kommentare im Code.

## 1. Erste Woche — der geführte Pfad

**Tag 1 — Bauen und Testen:**

```bash
dotnet build CalloraVoipSdk.sln -c Release -p:CodeAnalysisTreatWarningsAsErrors=true
dotnet test tests/CalloraVoipSdk.ArchitectureTests -c Release
dotnet test CalloraVoipSdk.sln -c Release \
  --filter "FullyQualifiedName!~CalloraVoipSdk.Core.Tests&Category!=SoakLong&Category!=Interop"
```

**Tag 2 — Einen echten Call fahren.** Der schnellste Weg zu einem lebenden System ist der
Asterisk-Container aus den Interop-Tests plus das BasicCalling-Sample:

```bash
# Docker muss laufen; der Smoke-Test startet andrius/asterisk:22 mit PJSIP-Endpoint 6001
dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "Category=Interop"
```

Danach `examples/CalloraVoipSdk.Sample.BasicCalling` mit `-v` gegen den Container (oder
eine Fritz!Box/sipgate-Testkonto) laufen lassen — `-v` schaltet Debug-Logging ein
(`Program.cs:7-10`). Registrieren, wählen, auflegen; parallel das Log lesen.

**Tag 3 — Einen Call durch den Code tracen.** [`flows.md`](flows.md) Ablauf 1 neben den
Debugger legen: Breakpoints auf `PhoneLine.DialAsync`,
`SipCallSignalingService.InviteAsync`, `SipCallSessionTransactionService.SendInviteTransactionAsync`,
`CallMediaOrchestrator.OnMediaParametersNegotiated`, `RtpCallMediaSession.OnPacketReceived`.
Wer diese fünf Stationen einmal live gesehen hat, kennt das Rückgrat des SDK.

**Tag 4 — Test-Harness kennenlernen.** Je einen bestehenden Test dieser drei Sorten lesen
und mutieren (absichtlich brechen, Fehlermeldung ansehen, zurückbauen):

- L3-Signaling ohne Netz: ein Test auf `CapturingSipTransportRuntime`
  (z. B. `SipInviteSuccessAckTests`) — Fake-Transport mit `ResponseFactory` und
  Fehlerinjektion.
- L2-Media mit echten Sockets: `RtpMediaLoopback`-basierter Test aus dem InteropHarness.
- L0-Wire: `SipWireRobustnessTests` — Malformed-Input-Muster.

**Tag 5 — Erste Änderung.** Ein P3-Befund aus MAINTAINING.md §5 ist der ideale
Einstiegs-PR (klein, testbar, ohne Protokollrisiko), z. B. der `samples/`→`examples/`-Scan
in `EngineeringRulesTests.cs:117`. Dabei den kompletten Workflow üben: Regel in
ENGINEERING_RULES.md prüfen, Test zuerst, Architektur-Gates lokal laufen lassen.

## 2. Diagnose-Werkzeuge

### SIP-Wire-Trace

`SipWireTraceLogger` loggt jede gesendete/empfangene SIP-Nachricht inklusive SDP-Body auf
**`LogLevel.Trace`** (Kategorie `SipTransportRuntime` bzw. die jeweilige Transportklasse).
Aktivierung: die per `VoipConfiguration.LoggerFactory` übergebene Factory auf Trace
stellen (z. B. `builder.SetMinimumLevel(LogLevel.Trace)` oder gezielt
`AddFilter("CalloraVoipSdk", LogLevel.Trace)`). Alle Einstiege sind mit
`IsEnabled(Trace)` geguardet — ausgeschaltet kostet der Trace nichts. **Secrets werden
redigiert**: SDES-`inline:`-Keys und `a=ice-pwd:` erscheinen nie im Log; das muss bei
jeder Erweiterung des Trace-Loggings erhalten bleiben (K5).

### Telemetrie

Drei Zugänge, alle gespeist aus demselben `ISipTelemetrySink`:

1. **Konsumenten-Events:** `voipClient.Telemetry.EventPublished/MetricPublished/CdrPublished`
   (`ITelemetryManager`) — der schnellste Weg, um Ereignisse (z. B.
   `sip.media.srtp.policy.decision`, ICE-Events) live zu sehen.
2. **Eigener Sink:** `ISipTelemetrySink` per DI registrieren (ersetzt den Null-Sink;
   der Client dekoriert ihn mit `ClientTelemetrySink`, beide Wege bleiben aktiv).
3. **In Tests:** `InMemorySipTelemetrySink` (Ringpuffer 4096/Stream) einstecken und
   Records assertieren.

### Beobachtbarkeit am Call

`ICall` trägt Snapshots für die drei häufigsten Fragen: `QualitySnapshot`
(Jitter/Loss/RTT/MOS aus RTCP), `RtpStatistics` (rohe RFC-3550-Zähler),
`IceSnapshot` + `IceConnectionState` (gewähltes Paar, Consent-Status). WebRTC-seitig:
`peer.GetStats()` (`WebRtcStats` — Philosophie „null statt 0" für Ungemessenes;
`SuppressedSends` > 0 heißt: es wurde vor Key-Installation gesendet, fail-closed griff).

### Soak-Artefakte

`SOAK_ARTIFACT_DIR` setzen ⇒ `SoakArtifactSink` schreibt JSON-Messreihen + `summary.md`
**vor** den Assertions — auch Fehlläufe hinterlassen ihre Daten. Nightly-Artefakte hängen
am `soak.yml`-Workflow-Run.

## 3. Bugs reproduzieren — welches Werkzeug wofür

| Symptom | Werkzeug |
|---|---|
| SIP-Verhalten (Auth, Dialog, Timer, Header) | `CapturingSipTransportRuntime`-Test auf L3 — Responses/Provisionals frei injizierbar, `ThrowOnSendPredicate` für Transportfehler |
| Medien/RTP/Jitter/SRTP | `RtpMediaLoopback` (zwei echte Sessions über UDP-Loopback, Matrix Codec × Plain/SRTP) bzw. direkter `RtpSession`-Test |
| DTLS/Keying | `DtlsMediaPathE2eTests`-Muster: zwei Attachments über Loopback |
| STUN/TURN-Server-Verhalten | `RawTurnUdpClient` (spricht echtes Wire-Format, stabiles 5-Tupel) |
| WebRTC end-to-end | `WebRtcPeerToPeerTests`-Muster: zwei Peers, In-Memory-Signalling |
| Fremd-Stack-Verhalten | `AsteriskContainer` (Testcontainers); ohne Docker skippen die Tests **still grün** — lokal immer prüfen, dass sie wirklich liefen |
| Leaks/Drift | SoakTests mit reduzierten Env-Parametern (`SOAK_ITERATIONS=50 …`) |

Neue Regressionstests auf der **niedrigsten sinnvollen Ebene** platzieren (K8):
Wire-Parsing → L0, Krypto → L1, RTP-Verhalten → L2, Dialog/Auth → L3, Fassade/DI →
Client.Tests, Fremd-Stack → InteropTests. Committe keinen Fix ohne Test auf der Ebene,
auf der der Bug lebt.

## 4. Stolperfallen (die jeder Neue einmal trifft)

1. **Zwei ICE-Implementierungen:** produktiv ist der `IceNominationDriver`
   (Shared-Socket); `IceConnectivityScheduler`/`IceCheckList` ist der klassische
   RFC-Scheduler und wird vom `CallIceAgent` genutzt. Vor ICE-Änderungen klären, welcher
   Pfad betroffen ist.
2. **Zwei Session-Familien mit ungleichem Reifegrad:** Einzelstream (SIP) hat
   Jitter-Buffer/PLC/RTX/TWCC; Bundle (WebRTC) transportseitig komplett, aber ohne
   NACK/RTX/PLI-Feedback-Pfad. Ein „funktioniert bei SIP"-Feature existiert auf dem
   Bundle nicht automatisch.
3. **`internal` ist produktweite API:** `InternalsVisibleTo` öffnet Core-Internals für
   Client, Audio-Pakete, Tests und InteropHarness. Signaturänderungen an internals
   brechen halbe Solution — vorher `grep` über alle Projekte.
4. **Events auf SDK-Threads:** nie im Handler blockieren (Deadlock mit `_operationGate`
   möglich) und nie werfen. Async-Arbeit: eigene Queue/`Task.Run` im Handler.
5. **Gathering vor Start:** ICE-/TURN-Gathering teilt den Media-Socket — WebRTC:
   `GatherCandidatesAsync` **vor** `StartAsync`, sonst fehlen Kandidaten.
6. **Testparallelisierung ist absichtlich aus** (Core.IntegrationTests, SoakTests):
   echte Sockets/Timer, prozessweite Messungen. Nicht „optimieren".
7. **Baselines nur schrumpfen:** Wer eine Altlast behebt, muss den Baseline-Eintrag in
   `EngineeringRulesTests` im selben Commit entfernen — sonst ist CI rot (gewollt).
8. **`VoipOptions` erweitern heißt drei Stellen:** Options + Configuration + Mapping;
   der Reflection-Guard `VoipOptionsMappingCompletenessTests` erzwingt es.
9. **Wanduhr vs. Monotonik:** neue Zeitlogik im Medienpfad monoton (`Stopwatch`) bauen;
   mehrere Bestandspfade nutzen noch `UtcNow` (bekannte Befunde, nicht nachahmen).
10. **Dispose-Reihenfolgen sind Verträge** (siehe threading-map.md §5) — insbesondere
    close_notify vor Cancel (DTLS) und Tap-Detach vor Sink-Complete (Recording).
