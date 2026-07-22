# Maintainer-Handbuch — CalloraVoipSdk

Zielgruppe: Entwickler, die dieses Repo warten und weiterentwickeln (nicht: SDK-Konsumenten —
deren Doku liegt unter `docs/portal/` und wird via DocFX veröffentlicht).

Ergänzende Dokumente:

| Dokument | Inhalt |
|---|---|
| [`ENGINEERING_RULES.md`](ENGINEERING_RULES.md) | Verbindliche Regeln; mechanisch per ArchitectureTests erzwungen |
| [`docs/maintainers/flows.md`](docs/maintainers/flows.md) | Die fünf Kern-Abläufe als Sequenz-Walkthroughs (Klassenkette + Thread je Schritt) |
| [`docs/maintainers/threading-map.md`](docs/maintainers/threading-map.md) | Threading-/Ownership-Karte: Loops, Event-Thread-Verträge, Locks, Sockets, Dispose-Reihenfolgen |
| [`docs/maintainers/onboarding-debugging.md`](docs/maintainers/onboarding-debugging.md) | Erste-Woche-Pfad, Diagnose-Werkzeuge (Wire-Trace, Telemetrie, Harness), Stolperfallen |
| [`docs/maintainers/repo-setup.md`](docs/maintainers/repo-setup.md) | Einmalige GitHub-Weboberflächen-Schritte: Label-Farben, Security-Reporting, Discussions, Branch-Protection |
| [`docs/audit/CODE_FINDINGS_REGISTER.md`](docs/audit/CODE_FINDINGS_REGISTER.md) | Register der im Code referenzierten Marker (CF-xxx, HARD-xxx, ADR-xxx) |
| [`docs/audit/INTEROP_SOAK_AUDIT.md`](docs/audit/INTEROP_SOAK_AUDIT.md) | Lebendes Fehlerregister aus Interop-/Soak-Audits (F001–F004) |
| [`docs/audit/2026-07-22-quelltext-tiefenanalyse.md`](docs/audit/2026-07-22-quelltext-tiefenanalyse.md) | Vollständige Tiefenanalyse (Klassenkataloge aller Subsysteme, Befunde mit Datei:Zeile) — datierter Referenzstand |
| `docs/audit/2026-07-21-interop-soak-audit-design.md` | Testebenen-Modell L0–L4, Soak-Methodik |

---

## 1. Architektur-Landkarte

### 1.1 Projekte

```
src/
├── Core/          Kompletter Protokoll-Stack (kein externes SIP/RTP/STUN — alles Eigenbau)
│   ├── Domain/          Aggregate Call/PhoneLine, Zustandsautomaten, Domain-Events, Value Objects
│   ├── Application/     Use-Case-Orchestrierung (CallManager, CallMediaOrchestrator, MediaManager,
│   │                    CallIceAgent, Recording/Playback) + Ports (Audio, Video, Sdp, Connectivity, Media)
│   ├── Infrastructure/  Sip | Sdp | Rtp | Rtcp | Srtp | Dtls | Stun | Turn | Media | WebRtc | Common
│   └── Sdk/             Öffentliche ICE-Konfigurations-DTOs (Namespace CalloraVoipSdk)
├── Client/        Öffentliche Fassaden + DI
│   ├── Application/     VoipClient/IVoipClient (Kompositionswurzel), Manager, Workflows, Module
│   ├── WebRtc/          WebRtcClient/IPeerConnection (4.6-Preview, transport-only)
│   ├── Hosting/         StunServerHost/TurnServerHost (eigenes Server-Hosting)
│   └── Infrastructure/  AddCalloraVoip(...), Options→Configuration-Mapping, HostedService
├── Audio/         Abstractions | Headless | Linux (PortAudio) | Windows (NAudio)
│                  — Plattformpakete werden zur Laufzeit per Reflection nachgeladen
└── CalloraVoipSdk/  Meta-Paket (Client + Audio.Abstractions)
```

Abhängigkeitsrichtung (mechanisch erzwungen, siehe ENGINEERING_RULES R1/R2):
Domain ← Application ← Infrastructure; Client verdrahtet Core-Ports per
„resolve-or-default" aus dem DI-Container. Internals sind per `InternalsVisibleTo`
(`src/Core/Properties/AssemblyInfo.cs`) für Client, Audio-Pakete, Tests und InteropHarness
sichtbar — **internal im Core ist damit de facto produktweite API**; Refactorings interner
Typen sind entsprechend teuer.

### 1.2 Die zwei Session-Familien im Medien-Stack

| | Einzelstream (SIP-Calls) | BUNDLE (WebRTC) |
|---|---|---|
| Einstieg | `RtpCallMediaSession` (+ `VideoRtpStream`) | `BundledMediaSession` (+ `Bundled*`-Kollaboratoren) |
| Socket | einer pro m-line | ein 5-Tupel für alles (RFC 8843), MID/RID-Demux |
| Keying | SDES (RFC 4568) **oder** DTLS-SRTP | nur DTLS-SRTP |
| Jitter/PLC | adaptiver `JitterBuffer` + Playout-Loop + Concealment | kein Jitter-Buffer (dokumentiert); Video: `VideoReorderBuffer` |
| Reparatur/Feedback | NACK/RTX, PLI, TWCC vollständig | **noch nicht** (Follow-ups in `BundledCallMediaSession`) |

### 1.3 Die zwei Fassaden (ADR-012, „Two-Facade Composition")

`VoipClient` (SIP) und `WebRtcClient` (WebRTC) spiegeln dasselbe Muster:
mutable `*Options` → pure Mapping-Funktion → immutable `*Configuration` → Client →
Fluent-Builder-Overrides (`PostConfigure`) → Modul-Registry als Plugin-Seam
(`IVoipClientModule` / `IWebRtcClientModule`). Die WebRTC-Fassade ist erklärter
**Preview-Stand**: nicht browser-validiert, kein SCTP/Datachannel, kein TCP/TLS-TURN,
Empfangs-Simulcast (RID-Demux) offen.

### 1.4 Konnektivität

STUN/TURN sind als Client **und** Server implementiert (Server-Hosting über
`Client/Hosting`). ICE ist zweigeteilt: reine Entscheidungslogik in
`Core/Application/Media/Ice`, Verdrahtung in `Core/Infrastructure/Stun/Ice`. Achtung:
Es existieren **zwei** ICE-Läufer — der klassische `IceConnectivityScheduler` und der
produktiv genutzte `IceNominationDriver` (Shared-Socket). Bei ICE-Arbeit zuerst klären,
welcher Pfad betroffen ist.

---

## 2. Invarianten, die man nicht brechen darf

Kurzfassung — Details und Fundstellen in [`ENGINEERING_RULES.md`](ENGINEERING_RULES.md):

1. **Fail-closed-Keying** (K1): nie Klartext senden/akzeptieren, wenn SRTP/DTLS verhandelt
   oder gefordert ist. Jeder neue Sende-/Empfangspfad braucht die Suppression-Prüfung.
2. **Enricher-Reihenfolge ICE → SRTP → DTLS** auf `CallMediaParameters` (K2); Änderungen
   nur als `with`-Klone (HARD-R5).
3. **Event-Handler-Snapshot unter Lock, Invocation außerhalb** (K3); Events feuern synchron
   auf SDK-Threads — niemals blockierende Arbeit in Handlern erzeugen.
4. **Nie auf dem Medien-Hotpath blockieren oder unbounded puffern** (K3): bounded Channels
   mit Drop-Oldest, Copy-on-write-Listenerlisten, keine vermeidbaren Allokationen.
5. **Atomare Snapshots für paarige Zustände** (HARD-C1/C2) statt feldweisem Lesen.
6. **Try-Parse-Vertrag an Vertrauensgrenzen** (K4): Wire-Decoder werfen nicht; malformter
   Input wird geloggt verworfen; DoS-Kappen für jeden neuen Parser/Listener.
7. **Secrets nie in Logs** (K5); Session-Keys beim Dispose nullen; konstantzeitige Vergleiche.
8. **Kein TODO — Marker- und Follow-up-System** (K6): behobene Findings tragen CF-/HARD-Marker,
   offene Punkte strukturierte Follow-up-Kommentare. Neue Findings ins Register eintragen.
9. **RFC-Verweis mit Paragraph** an jedem Protokollverhalten; Abweichungen begründen (K7).
10. **Baselines der Architektur-Tests dürfen nur schrumpfen** — wer eine Altlast behebt,
    entfernt den Baseline-Eintrag im selben Commit.

---

## 3. Arbeitsabläufe

### 3.1 Toolchain & Build

- .NET SDK **10.0.100** (`global.json`, rollForward latestFeature); Ziel-TFMs
  `net8.0;net9.0;net10.0` überall (ArchitectureTests nur net10.0).
- Version kommt aus `src/Directory.Build.props` (Fallback `4.6.0-preview.1`); Releases
  überschreiben per `/p:Version` aus dem Git-Tag.

```bash
dotnet build CalloraVoipSdk.sln --configuration Release
```

CI baut mit `-p:CodeAnalysisTreatWarningsAsErrors=true` — lokal vor dem Push genauso bauen,
sonst scheitert der PR an Analyzer-Warnungen.

### 3.2 Tests (Ebenenmodell L0–L4)

```bash
# 1. Architektur-Gates (laufen in CI zuerst)
dotnet test tests/CalloraVoipSdk.ArchitectureTests --configuration Release

# 2. Standard-Testlauf (wie CI: ohne Long-Soaks, ohne Interop)
dotnet test CalloraVoipSdk.sln --configuration Release \
  --filter "FullyQualifiedName!~CalloraVoipSdk.Core.Tests&Category!=SoakLong&Category!=Interop"

# 3. Interop (braucht laufenden Docker-Daemon; ohne Docker: stiller Skip!)
dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "Category=Interop"

# 4. Long-Soaks (nightly; lokal mit reduzierten Parametern)
SOAK_ITERATIONS=50 SOAK_DURATION_SECONDS=10 SOAK_ARTIFACT_DIR=/tmp/soak \
  dotnet test tests/CalloraVoipSdk.SoakTests -f net10.0 --filter "Category=SoakLong"
```

Wissenswertes:
- Core.IntegrationTests und SoakTests haben **Parallelisierung deaktiviert** (echte
  Sockets/Timer, prozessweite Messungen) — nicht wieder aktivieren.
- Soak-Umgebungsvariablen: `SOAK_ITERATIONS`, `SOAK_WAVES`, `SOAK_PARALLELISM`,
  `SOAK_DURATION_SECONDS`; `SOAK_ARTIFACT_DIR` aktiviert den JSON-Artefakt-Sink
  (Artefakte werden **vor** den Assertions geschrieben — auch Fehlläufe hinterlassen Daten).
- Interop-Tests skippen ohne Docker **grün** (`DockerRequiredFactAttribute`) — ein grüner
  Interop-Job beweist nichts, wenn Docker fehlte.
- Der CI-Filter schließt das nicht existente `CalloraVoipSdk.Core.Tests` aus (Altlast).

### 3.3 Performance-Gate

```bash
# Benchmark laufen lassen und gegen Baseline prüfen (Default: 15 % Toleranz)
dotnet run --project perf/CalloraVoipSdk.Core.Performance -c Release -- \
  --gate perf/baselines/core-performance-baseline.json

# Neue Baseline schreiben (nur bewusst, auf Referenz-Hardware)
dotnet run --project perf/CalloraVoipSdk.Core.Performance -c Release -- \
  --write-baseline perf/baselines/core-performance-baseline.json
```

Achtung (Stand 2026-07-22): Das Gate wird **von keinem CI-Workflow aufgerufen**; die
Baseline stammt von net8/2026-04. `Conferencing.Performance` referenziert ein nicht
existentes Projekt und baut nicht; `Media.Performance` ist ein Skelett. Wer Perf-relevante
Änderungen macht, führt das Gate manuell aus.

### 3.4 Release

1. Tag `v*` pushen (oder `packages.yml` per Dispatch mit Versions-Input starten).
2. Workflow baut, testet (derzeit **ungefiltert**, inkl. Long-Soaks — bekannte Schwäche),
   packt 6 Pakete (Core, CalloraVoipSdk, Client, Audio.Abstractions, Audio.Windows,
   Audio.Linux) und pusht nach nuget.org (`NUGET_API_KEY`, `--skip-duplicate`).
3. Doku: `release-docs.yml` deployt DocFX nach GitHub Pages (root + versioniert) bei Push
   auf main; `docs.yml` ist das PR-Gate für den Doku-Build.
4. `CHANGELOG.md` pflegen; Breaking Changes im README-Abschnitt „What's new" nachziehen.

### 3.5 Doku-Grenzen

- `docs/portal/` = Consumer-Doku (DocFX; `filterConfig.yml` blendet `[Obsolete]`,
  `Core.Infrastructure.*` und `Core.Application.Ports.*` aus der API-Referenz aus).
- `docs/audit/` = Audit-/Maintainer-Artefakte (Register, Analysen).
- `.gitignore` erlaubt unter `docs/` nur `portal` und `audit` — neue Doku-Verzeichnisse
  brauchen eine Whitelist-Zeile.

---

## 4. Subsystem-Einstiegspunkte

Vollständige Klassenkataloge: Tiefenanalyse 2026-07-22. Hier nur die Türen, durch die man
ein Subsystem betritt:

| Subsystem | Schlüsselklassen (Einstieg → Tiefe) |
|---|---|
| **SIP Wire/Transport** | `SipWireProtocol` (Codec) → `SipTransportRuntime` (5 Transporte, Listener/Sende-Multiplexer) → `SipOutboundConnectionPool`/`SipStreamConnection`/`SipWireStreamFramer` |
| **SIP Transaktionen** | UAC: `SipClientTransactionExecutor` (Timer A/B/E/F/D/K) · UAS: `SipServerTransactionEngine` + `SipServerTransactionKey` (§17.2.3-Matching) |
| **SIP Dialoge/Signaling** | `SipCallSignalingService` (zentraler Ingress-Dispatcher) → `SipCallSession` (Fassade) mit `…HeaderService`/`…TransactionService`/`…InboundService` → `SipDialogManager` (Forking) |
| **SIP↔Domain-Adapter** | `SipCoreCallChannel` (`ICallChannel`), `SipLineChannel` (`ILineChannel`, REGISTER-Loop, NAT-Lernen), `TrunkInboundMatcher` |
| **SDP** | `SdpSessionParser`/`-Serializer` → `SdpOfferAnswerNegotiator` (RFC 3264-Kern) → Fassade `SdpUtilities`/`SdpNegotiator` (Port `ISdpNegotiator`) |
| **RTP Einzelstream** | `RtpSession` (Socket/Demux/SRTP) → `RtpCallMediaSession` (Jitter/PLC/DTMF) → `VideoRtpStream` (RTX/PLI/TWCC) |
| **RTP BUNDLE** | `BundledMediaSession` (Komposition) → `BundledMediaTransport` / `BundledInboundPipeline` / `BundledOutboundPipeline` / `BundledRtcpReporter` / `BundledRtpDemultiplexer` |
| **SRTP/DTLS** | `SrtpContext`/`SrtcpContext` (per Richtung, per-SSRC-ROC/Replay) · `DtlsSrtpHandshaker` + `DtlsMediaAttachment` (Handshake → Key-Export → Kontext-Installation) |
| **STUN/TURN/ICE** | `StunMessageCodec` (einziger Wire-Ort) · `StunClient`/`StunServer` · `TurnClient`/`TurnServer` (+ `TurnRelayControlClient` für Shared-Socket) · `IceMediaAttachment` (bündelt Inbound-Handler, Consent, `IceNominationDriver`) |
| **Core-Orchestrierung** | `CallMediaOrchestrator` (Session-Lebenszyklus je Call) · `CallIceAgent` · `CallRtcpQualityMonitor` · `MediaManager` (Recording/Playback) |
| **Domain** | `Call` + `CallStateRules` (Übergangstabelle) · `PhoneLine` · Ports `ICallChannel`/`ILineChannel`/`ICallRegistry` |
| **Client-Fassade** | `VoipClient`-Konstruktor = Kompositionswurzel (resolve-or-default aller Ports) · `SdkConvenienceOrchestrator` (Connect/Dial-Workflows) · `ServiceCollectionExtensions`/`CalloraBuilder` |
| **WebRTC-Fassade** | `WebRtcClient` → `PeerConnection` (Adapter) → Core-`WebRtcPeerConnection` → `WebRtcSessionFactory` (→ `BundledMediaSession`) · Happy-Path: `WebRtcPeerConnectionExtensions.ConnectAsync` |
| **Audio** | Port `IAudioDevice`/`IAudioDeviceRuntimeControl` · `PlatformAudioDeviceFactory` (Reflection-Load) · `LinuxAudioDevice`/`WindowsAudioDevice` · geteilt: `BoundedPlaybackBuffer`, `PcmGain` |
| **Test-Harness** | `SourceScan` (Architektur-Gates) · `RtpMediaLoopback`/`SipRegisterLoopHarness` (InteropHarness) · `CapturingSipTransportRuntime` (SIP-Fakes) · `AsteriskContainer` (Interop) |

Typische Erweiterungspunkte:
- **Neuer Audio-Codec (Datei/Bridge):** `PayloadCodecKind` + `AudioPayloadTranscoder`
  (Application/Media/Sessions); Geräteseite in `LinuxAudioDevice`/`WindowsAudioDevice`
  (Achtung: Logik ist dort dupliziert — Kandidat für Extraktion à la `PcmGain`).
- **Neues SDK-Feature als Modul:** Feature-Interface + `IVoipClientModule`-Implementierung,
  DI-Registrierung — die Registry sammelt es automatisch ein (`OnAttached` läuft vor
  Sichtbarkeit).
- **Neue VoipOptions-Eigenschaft:** Feld in `VoipOptions` **und** `VoipConfiguration` **und**
  `VoipOptionsMapping` — der Reflection-Drift-Guard `VoipOptionsMappingCompletenessTests`
  schlägt sonst fehl (gewollt).
- **Neuer SIP-Header/-Mechanismus:** Policy als statische Pure-Function-Klasse (Vorbild
  `SipSessionTimerPolicy`, `SipRequireOptionPolicy`), Verdrahtung in
  `SipCallSessionInboundService`/`…TransactionService`.

---

## 5. Bekannte Baustellen (Stand 2026-07-22)

Quellen: `docs/audit/INTEROP_SOAK_AUDIT.md` (F-Register) und Tiefenanalyse 2026-07-22
(dortiges Kapitel „Konsolidierte Top-Befunde" mit Datei:Zeile). Die wichtigsten, nach
Priorität:

**P1 — Interop/Stabilität (sollten Issues werden):**
1. SIP: kein Re-ACK auf retransmittierte 2xx (Call-Abbau bei ACK-Verlust möglich).
2. SIP: kein Digest-Retry auf Session-Timer-UPDATE und SUBSCRIBE-Refresh.
3. SIP: `+sip.instance`-Contact-Parameter malformiert (Name gequotet).
4. SRTP: SRTCP-Auth-Tag bei `*_HMAC_SHA1_32`-Suiten 4 statt 10 Bytes (libsrtp-Interop-Bruch).
5. TURN-Server: verlangt MESSAGE-INTEGRITY auf Send-Indications (RFC-widrig) und kann
   Loopback als Relay-Adresse annoncieren (fehlende konfigurierbare öffentliche Adresse).
6. Core: Transfer kann in `Transferring` hängenbleiben (fehlendes try/finally);
   ICE-Terminierungs-Race leakt Media-Sessions.
7. Audio: G.722-Transkodierung zustandslos (Artefakte); 8-KiB-Kernel-Empfangspuffer auf
   allen Media-Sockets.

**P2 — Bekannte Register-Befunde:** F002 (Late-Drops als `PacketsUnrecoverableLoss`
fehlklassifiziert; Repro-Soak geskippt, wird nach Fix automatisch zur Verifikation),
F003 (keine Zeitabstraktion im Signaling → kein Zeitraffer-Soak), F004 (RTT auf L2 ist
statischer Hint — Wächter-Test schlägt an, wenn sich das ändert).

**P3 — Infrastruktur:** Perf-Gate nicht in CI verdrahtet; `Conferencing.Performance`
verwaist; Release-Gate ungefiltert (Long-Soaks im Release-Pfad); Interop-Abdeckung nur
REGISTER; `EngineeringRulesTests` scannt `samples/` statt `examples/`; Coverage ohne
Schwellwert; `InternalsVisibleTo` auf nicht existente Assemblies.

**Erklärte Preview-/Scope-Grenzen (keine Bugs):** WebRTC-Fassade nicht browser-validiert;
kein SCTP; kein TCP/TLS-TURN-Gathering; Bundle-Pfad ohne NACK/RTX/PLI/TWCC; kein volles ICE
im SIP-Remote-Endpoint-Pfad; mDNS-Kandidaten ignoriert; Simulcast empfangsseitig offen.
