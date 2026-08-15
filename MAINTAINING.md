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
│   ├── WebRtc/          WebRtcClient/IPeerConnection (transport-only)
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
| Reparatur/Feedback | NACK/RTX, PLI/FIR, transport-cc vollständig (`VideoRtpStream`) | seit 4.6.0 ebenfalls vollständig: NACK/PLI/FIR (`VideoKeyFrameFeedback`, `VideoArrivalLossTracker`), RTX (`Retransmission/`), transport-cc über **eine** transportweite Ebene je Bundle (`BundledCongestionPlane`) |
| RTCP | `CallRtcpQualityMonitor` (L3) | `BundledRtcpReporter` (SR/RR, per-SSRC-Reception, RTT) im Bundle selbst |

### 1.3 Die zwei Fassaden (ADR-012, „Two-Facade Composition")

`VoipClient` (SIP) und `WebRtcClient` (WebRTC) spiegeln dasselbe Muster:
mutable `*Options` → pure Mapping-Funktion → immutable `*Configuration` → Client →
Fluent-Builder-Overrides (`PostConfigure`) → Modul-Registry als Plugin-Seam
(`IVoipClientModule` / `IWebRtcClientModule`). Die WebRTC-Fassade ist seit 4.6.0
browser-validiert (Chromium + Firefox, beide Rollen, in CI). Erklärte Scope-Grenzen:
kein SCTP/Datachannel, kein TCP/TLS-TURN, Empfangs-Simulcast (RID-Demux) offen.

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
- Version kommt aus `src/Directory.Build.props` (`VersionPrefix` + `VersionSuffix`, aktuell
  `4.6.0`); Releases überschreiben per `/p:Version` aus dem Git-Tag.

```bash
dotnet build CalloraVoipSdk.sln --configuration Release
```

CI baut mit `-p:CodeAnalysisTreatWarningsAsErrors=true` — lokal vor dem Push genauso bauen,
sonst scheitert der PR an Analyzer-Warnungen.

⚠️ **Dabei `--no-incremental` setzen.** Ein inkrementeller Build überspringt Projekte, die er für aktuell
hält, und mit ihnen die CA-Analyzer — derselbe Code meldete erst „3 Fehler", beim Wiederholungslauf
„0 Fehler", ohne dass sich etwas geändert hatte. Wer daraus „grün" schließt, übersieht genau die Regeln,
für die das Gate existiert (konkret fast passiert bei `CA5350`, schwacher Hash-Algorithmus):

```bash
dotnet build src/Core/CalloraVoipSdk.Core.csproj -c Release -warnaserror --no-incremental
```

### 3.2 Tests (Ebenenmodell L0–L4)

```bash
# 1. Architektur-Gates (laufen in CI zuerst)
dotnet test tests/CalloraVoipSdk.ArchitectureTests --configuration Release

# 2. Standard-Testlauf (exakt der CI-Filter des build-and-test-Jobs)
dotnet test CalloraVoipSdk.sln --configuration Release \
  --filter "Category!=SoakLong&Category!=Interop&Category!=InteropFreeSwitch&Category!=BrowserInterop&Category!=Chaos&Category!=Perf&FullyQualifiedName!~ArchitectureTests"

# 3. Interop Asterisk (braucht laufenden Docker-Daemon; ohne Docker: stiller Skip!)
dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 \
  --filter "Category=Interop&Category!=InteropLocalMedia&Category!=InteropFreeSwitch"

# 4. Interop FreeSWITCH (nicht im PR-Gate — lokal ausführen)
dotnet test tests/CalloraVoipSdk.InteropTests -f net10.0 --filter "Category=InteropFreeSwitch"

# 5. Browser-Interop (Chromium + Firefox über Playwright)
dotnet test tests/CalloraVoipSdk.BrowserInteropTests -f net10.0 --filter "Category=BrowserInterop"

# 6. Chaos- und Perf-Gate (beide eigene PR-Jobs)
dotnet test tests/CalloraVoipSdk.SoakTests -f net10.0 --filter "Category=Chaos"
dotnet test tests/CalloraVoipSdk.SoakTests -f net10.0 --filter "Category=Perf"

# 7. Long-Soaks (nightly via soak.yml; lokal mit reduzierten Parametern)
SOAK_ITERATIONS=50 SOAK_DURATION_SECONDS=10 SOAK_ARTIFACT_DIR=/tmp/soak \
  dotnet test tests/CalloraVoipSdk.SoakTests -f net10.0 --filter "Category=SoakLong"
```

CI-Jobs in `ci.yml`: `build-and-test` (Architektur-Gates + Standardlauf + Coverage), `interop`
(Asterisk), `chaos`, `perf`, `browser-interop` (Chromium + Firefox). `soak.yml` fährt `SoakLong`
nächtlich, `packages.yml` ist der Release-Pfad.

Wissenswertes:
- Core.IntegrationTests und SoakTests haben **Parallelisierung deaktiviert** (echte
  Sockets/Timer, prozessweite Messungen) — nicht wieder aktivieren.
- Soak-Umgebungsvariablen: `SOAK_ITERATIONS`, `SOAK_WAVES`, `SOAK_PARALLELISM`,
  `SOAK_DURATION_SECONDS`; `SOAK_ARTIFACT_DIR` aktiviert den JSON-Artefakt-Sink
  (Artefakte werden **vor** den Assertions geschrieben — auch Fehlläufe hinterlassen Daten).
- Interop-Tests skippen ohne Docker **grün** (`DockerRequiredFactAttribute`) — ein grüner
  Interop-Job beweist nichts, wenn Docker fehlte. Analog skippen Browser-Tests ohne installierte
  Engine (`BrowserFactAttribute`); WebKit/Safari ist genau deshalb unverifiziert.
- Browser-Interop läuft über `BrowserEngine` (Chromium/Firefox/WebKit) gegen die statischen
  Peer-Seiten `peer.html` / `peer-offerer.html` / `peer-video.html` und die
  `BrowserInteropSignalingBridge`.

### 3.3 Performance-Gate

```bash
# Benchmark laufen lassen und gegen Baseline prüfen (Default: 15 % Toleranz)
dotnet run --project perf/CalloraVoipSdk.Core.Performance -c Release -- \
  --gate perf/baselines/core-performance-baseline.json

# Neue Baseline schreiben (nur bewusst, auf Referenz-Hardware)
dotnet run --project perf/CalloraVoipSdk.Core.Performance -c Release -- \
  --write-baseline perf/baselines/core-performance-baseline.json
```

**Zwei verschiedene Dinge nicht verwechseln (Stand 2026-07-28):**

- Der **PR-Perf-Job** in `ci.yml` (`perf`) läuft `Category=Perf` aus den SoakTests
  (`tests/CalloraVoipSdk.SoakTests/Perf/SrtpPerfGateTests.cs`) und hält den SRTP-Paket-Hotpath
  über einem großzügigen Durchsatz-Boden — er fängt katastrophale Regressionen (Sync-over-async,
  O(n²), Allokationsstürme), ohne an CI-CPU-Varianz zu flaken.
- Der **Benchmark-Gate oben** (`perf/CalloraVoipSdk.Core.Performance` gegen
  `perf/baselines/core-performance-baseline.json`) ist davon unabhängig und wird **weiterhin von
  keinem Workflow aufgerufen**; die Baseline stammt von net8/2026-04. Wer perf-relevante Änderungen
  macht, führt ihn manuell aus.

Unter `perf/` liegt nur noch `CalloraVoipSdk.Core.Performance`. Die früheren Geschwister
`Conferencing.Performance` (referenzierte ein nie existierendes `src/Modules/Conferencing/…`) und
`Media.Performance` (Skelett, druckte nur „skeleton") sind entfernt — neue Hotpath-Messungen gehören
als `Category=Perf`-Floor in `tests/CalloraVoipSdk.SoakTests/Perf/`, weil nur der im CI läuft.

### 3.4 Coverage-Gate

```bash
# Lauf mit Coverage (exakt der CI-Filter), dann prüfen
dotnet test CalloraVoipSdk.sln --configuration Release \
  --filter "Category!=SoakLong&Category!=Interop&Category!=InteropFreeSwitch&Category!=BrowserInterop&Category!=Chaos&Category!=Perf&FullyQualifiedName!~ArchitectureTests" \
  --collect:"XPlat Code Coverage" --results-directory ./TestResults
python3 .github/scripts/check-coverage.py

# Baseline bewusst neu setzen (nach echter Verbesserung — oder nach begründetem Abfall)
python3 .github/scripts/check-coverage.py --update
```

Der Gate ist **relativ**: er vergleicht gegen `.github/line-coverage-baseline.json` und schlägt
an, wenn die Zeilenabdeckung mehr als die dort hinterlegte Toleranz (2 Punkte) darunter fällt.
Bewusst kein absoluter Zielwert — eine ausgedachte Zahl bestraft ehrliche Arbeit, und was zählt
ist, ob eine Änderung die Lage **verschlechtert**. Dasselbe Muster wie bei den
Architektur-Baselines (dürfen nur schrumpfen) und beim Perf-Gate (Boden statt Baseline). Die
Referenz macht es genauso: Pion gatet mit `threshold: 2%` ohne Projektziel, SIPSorcery erhebt
gar keine Coverage.

Zwei Fallen:
- Der Gate läuft **nur auf ubuntu-latest**. `Audio.Linux` ist auf Windows toter Code und
  umgekehrt — eine Zahl kann nicht beide Runner beschreiben.
- Ein Lauf erzeugt viele Cobertura-Reports (mehrere Testprojekte × TFMs) mit **unterschiedlichen
  `<source>`-Basen**. Das Skript normalisiert auf den `src/`-Anteil und vereinigt zeilenweise;
  wer stattdessen die Summen der Reports addiert, zählt dieselbe Zeile mehrfach und misst grob
  zu niedrig.

Baseline lokal nach einem **sauberen** Build erheben: liegen alte Artefakte in `bin/`, tragen
deren PDBs Pfade verschobener Dateien und verfälschen die Zahl.

### 3.5 Release

1. Tag `v*` pushen (oder `packages.yml` per Dispatch mit Versions-Input starten).
2. `packages.yml` baut, fährt das Release-Gate
   (`Category!=SoakLong&Category!=Interop&Category!=InteropFreeSwitch&Category!=BrowserInterop`
   — Chaos- und Perf-Tests laufen hier also mit), packt 6 Pakete (Core, CalloraVoipSdk, Client,
   Audio.Abstractions, Audio.Windows, Audio.Linux) und pusht nach nuget.org (`NUGET_API_KEY`,
   `--skip-duplicate`). Die Version kommt aus dem Tag (`v*` → `/p:Version`) bzw. dem
   Dispatch-Input.
3. Doku: `release-docs.yml` deployt DocFX nach GitHub Pages (root + versioniert) bei Push
   auf main; `docs.yml` ist das PR-Gate für den Doku-Build.
4. `CHANGELOG.md` pflegen; Breaking Changes im README-Abschnitt „What's new" nachziehen.

### 3.6 Doku-Grenzen

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
| **Client-Fassade** | `VoipClient`-Konstruktor = Kompositionswurzel (resolve-or-default aller Ports) · `SdkConvenienceOrchestrator` (Connect/Dial-Workflows) · `ServiceCollectionExtensions`/`CalloraBuilder` (in `VoipSdkBuilder.cs`) |
| **WebRTC-Fassade** | `WebRtcClient` → `PeerConnection` (Adapter) → Core-`WebRtcPeerConnection` → `WebRtcSessionFactory` (→ `BundledMediaSession`) · Happy-Path: `WebRtcPeerConnectionExtensions.ConnectAsync` |
| **Audio** | Port `IAudioDevice`/`IAudioDeviceRuntimeControl` · `PlatformAudioDeviceFactory` (Reflection-Load) · `LinuxAudioDevice`/`WindowsAudioDevice` · geteilt: `BoundedPlaybackBuffer`, `PcmGain` |
| **Test-Harness** | `SourceScan` (Architektur-Gates) · `RtpMediaLoopback`/`SipRegisterLoopHarness` (InteropHarness) · `CapturingSipTransportRuntime` (SIP-Fakes) · `AsteriskContainer`/`FreeSwitchContainer` hinter `IPbxFixture` (Interop) · `BrowserEngine`/`BrowserPeer`/`BrowserInteropSignalingBridge` (Browser-Interop) |

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

## 5. Bekannte Baustellen (Stand 2026-07-28, Release 4.6.0)

> **Nachtrag 4.8.0 (2026-08-06):** Seit dieser Bestandsaufnahme landeten die ICE-Setup-Latenz-Überarbeitung
> (4.7.2) und ein **stack-weiter Härtungs-Sweep** aus Review-Findings über DTLS/STUN/TURN/RTP/RTCP/SDP/SIP
> (4.8.0), plus per-Line-mTLS ([ADR-064](docs/adr/ADR-064-per-line-sip-mutual-tls.md)), eine öffentliche
> PCM-Transcoding-Fläche ([ADR-065](docs/adr/ADR-065-public-pcm-transcoding-surface.md)) und
> DTLS-post-handshake-`close_notify`-Servicing ([ADR-066](docs/adr/ADR-066-dtls-post-handshake-association-servicing.md)).
> Die einzelnen Punkte unten stammen aus dem 4.6.0-Stand — Details zu allem Neuen in
> [`CHANGELOG.md`](CHANGELOG.md) und [`RELEASE_NOTES_4.8.0.md`](RELEASE_NOTES_4.8.0.md).

Quellen: `docs/audit/INTEROP_SOAK_AUDIT.md` (F-Register) und die Tiefenanalyse 2026-07-22
(`docs/audit/2026-07-22-quelltext-tiefenanalyse.md`). **Sämtliche P1-Befunde der Tiefenanalyse
(SIP-Re-ACK, Digest-Retry auf UPDATE/SUBSCRIBE, `+sip.instance`, SRTCP-Auth-Tag, TURN-Send-Indication
und Relay-Adresse, Transfer-Hänger und ICE-Terminierungs-Race, G.722-Zustand, Socket-Empfangspuffer)
sind in 4.6.0 gefixt** — Details im [`CHANGELOG.md`](CHANGELOG.md). Was offen bleibt:

**Register-Befunde — alle geschlossen:**
- **F003 ist geschlossen** — `SipLineChannel` (Refresh-Loop + Recovery-Backoff) und
  `SipSessionTimerManager` (RFC-4028-Refresh/Expiry) nehmen jetzt einen `TimeProvider`
  (Default `TimeProvider.System`, Produktion unverändert). Ein Test kann damit Stunden
  Signaling-Zeit in Millisekunden fahren — `SipTimeProviderSeamTests` belegt 20 Registrierungs-
  zyklen à 3600 s. **Beim Schreiben eigener Zeitraffer-Tests:** die Schleife armiert ihren
  nächsten Timer erst *nach* dem Round-Trip; ein `Advance` in dieses Fenster geht ins Leere und
  der Test hängt bis zum Timeout. Wiederholt in Schritten vorspulen (siehe `AdvanceUntil` dort),
  statt einmal groß.
- **F004 ist kein Defekt** — die RTT-Kette existiert vollständig und ist produktiv verdrahtet:
  `CallMediaOrchestrator` hängt den `CallRtcpQualityMonitor` an jede Call-Media-Session, der
  rechnet nach RFC 3550 §6.4.1 und ruft `UpdateRoundTripTimeHint`, der JitterBuffer ersetzt damit
  seinen Seed. Belegt durch `QosMetricsTests` (RR mit LSR/DLSR → exakt 300 ms) und
  `JitterBufferRttSeedTests`. Der statische Hint erscheint **nur** im nackten L2-Loopback ohne
  Monitor — das hält der Wächter-Test fest, und dort gehört er auch hin.
- **F002 ist geschlossen** — der Late-Drop-Pfad rückt den Delivered-Sequence-Cursor mit, der
  Loopback-Soak `LongCall_UnrecoverableLoss_IsZeroOnLoopback` läuft ungeskippt.

**Offene Infrastruktur-Punkte:**
- Der Perf-Gate im CI misst nur die **Senderichtung** (SRTP `Protect`); der Empfangspfad
  (SRTP `Unprotect`, `RtpPacketCodec.Decode`, Jitterbuffer) hat keinen Floor.
- Der Coverage-Gate misst **Zeilen**, nicht Branches — die Cobertura-Reports enthalten
  `branch-rate`, das Skript wertet sie (noch) nicht aus.
- Die FreeSWITCH-Interop-Suite (`Category=InteropFreeSwitch`) läuft lokal, ist aber **nicht** im
  PR-Gate — Regressionen fallen nur beim expliziten Lauf auf.

*Erledigt seit der Tiefenanalyse:* Perf- und Chaos-Gate hängen als eigene Jobs in `ci.yml`;
`packages.yml` filtert Long-Soaks und Interop aus dem Release-Pfad; die Interop-Abdeckung ist von
„nur REGISTER" auf die volle Asterisk-Matrix plus Zwei-Bein-Bridge gewachsen; `EngineeringRulesTests`
scannt `examples/`; die vier `InternalsVisibleTo`-Grants auf nicht gebaute Assemblies sind entfernt
und `InternalsVisibleToTests` hält den Zustand (die Assemblies sind unsigniert, ein Grant ist also
nur ein Name — siehe `src/Core/Properties/AssemblyInfo.cs`).

**Erklärte Scope-Grenzen (keine Bugs):** kein SCTP/Datachannel; kein TCP/TLS-TURN-Relay;
Empfangs-Simulcast (RID-Demux) offen; kein volles ICE im SIP-Remote-Endpoint-Pfad; ICE-TCP
(RFC 6544) bewusst ausgelassen; Safari/WebKit nicht verifiziert. ICE selbst ist implementiert und
opt-in, aber in Produktions-Trunks unerprobt — Restarbeiten in
[#62](https://github.com/BechsteinDigital/callora-voip-sdk/issues/62).
