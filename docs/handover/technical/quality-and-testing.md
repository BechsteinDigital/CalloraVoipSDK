# Qualitäts- & Teststrategie

> Teil des technischen Due-Diligence-Pakets · Stand: 2026-07-27

Dieses Dokument beschreibt, **wie** die Qualität des CalloraVoipSdk gesichert wird: das
Ebenen-Testmodell, die real vorhandenen Testprojekte, die CI-Gates, die mechanische und
review-verbindliche Durchsetzung der Engineering-Regeln, die Audit-/Findings-Register-Disziplin
sowie — bewusst offen ausgewiesen — was **nicht** automatisiert getestet ist.

Die Aussagen sind an Code, Workflows und Registern belegt. Wo eine Zahl belegbar ist, steht sie;
wo nur eine qualitative Aussage tragfähig ist, steht keine erfundene Zahl. Der Grundsatz des
Projekts — "Testgrün allein ist kein Beweis für Status-Hochstufungen" — gilt auch für dieses
Dokument.

Referenz-Entscheidungen:
- Ebenenmodell + Interop/Soak-Harness: [ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md)
- Audit-Gedächtnis (Findings-Register + Code-Marker): [ADR-057](../../adr/ADR-057-audit-findings-register-marker-discipline.md)

---

## 1. Das Ebenen-Testmodell L0–L4

Der Kern der Teststrategie ist: **Fehler auf der Ebene isolieren, auf der sie entstehen** — nicht
erst als Symptom durch die Facade. Neue Funktionalität wird auf der **niedrigsten sinnvollen Ebene**
getestet (`ENGINEERING_RULES.md` K8), sodass ein Defekt mit einer zeilen-genauen Fundstelle landet
statt als diffuses Facade-Symptom.

| Ebene | Kern-Typ(en) | Prüft | Primär für |
|---|---|---|---|
| **L0 Wire** | SDP-Offer/Answer, SIP-Transport-Framer (UDP/TCP/TLS), STUN-Codec | Parsing/Serialisierung, malformed-Input, Framing | Robustheit, Fuzzing-artig |
| **L1 Security** | `SrtpContext`/`SrtcpContext`, DTLS | Verschlüsselung/Replay/Rekey unter Last | Krypto-Korrektheit, Security-Soak |
| **L2 Media** | `RtpCallMediaSession` | RTP/RTCP-Round-Trip, Jitter/Loss/SSRC | Media-Drift-Soak, Round-Trip-Verifikation |
| **L3 Signaling/Call** | `SipCoreCallChannel`, `ISipCallSession` | SIP-Dialog/Transaktion, 4xx/5xx, CANCEL, re-INVITE, Auth | Fehlerinjektion, Signaling-Soak |
| **L4 Facade/Interop** | `VoipClient` / `IVoipClient` | ganze Orchestrierung, zwei Instanzen E2E | Interop (Fremd-Stack), Realismus-Soak |

- **L0–L3 sind Loopback + Fehlerinjektion** — deterministisch und CI-tauglich, ohne externe
  Abhängigkeit. Sie liefern die zeilen-genaue Ursache.
- **L4 ist reale Fremd-Stack-Interop** — beweist Wire-Konformität am Rand gegen echte Peers.

Beide Aufgaben sind unterschiedlich und beide nötig: "isolate at the layer, prove at the edge"
(ADR-058). Die vollständige Definition des Ebenenmodells liegt in
`docs/audit/2026-07-21-interop-soak-audit-design.md` §4.1.

**Non-Happy-Path ist auf jeder Ebene erstklassig**, nicht nachgereicht: Reject 486/603,
Auth 401/407, CANCEL vor Answer, Timeout, BYE-Race, malformed SDP, abgelehnte re-INVITEs sind
Teil der Matrix by design.

### Kategorisierung über xUnit-Traits

Die Ausführungsschienen werden über `[Trait("Category", …)]` gesteuert. Real im Baum verwendet und
CI-verdrahtet sind drei Kategorien (belegt über Attribut-Vorkommen im `tests/`-Baum):

| Trait | Zweck | Läuft in | Vorkommen (Attribut) |
|---|---|---|---|
| `SoakShort` | kurzer Leak-/Drift-Lauf im PR-CI | PR/Push-CI (Hauptsuite) | 4 |
| `SoakLong` | langer Soak (Stunden/Zyklen) | nightly (`soak.yml`) | 6 |
| `Interop` | Docker-Fremd-Stack (Asterisk) | eigener CI-Job | 16 |

Die Trait-Zählungen sind die im Quellcode gesetzten `Trait`-Attribute; Theorien expandieren zur
Laufzeit auf mehr Einzelfälle. Die `Interop`-Suite enthält zusätzlich `[DockerRequiredFact]`-Fakten
(17 Vorkommen), die ohne verfügbaren Docker-Host übersprungen werden statt zu scheitern.

---

## 2. Testprojekte (real vorhanden)

Sieben Testprojekte, alle unter `tests/` verifiziert. Die Zahlen sind Quelldatei- und
Testattribut-Zählungen (`[Fact]`/`[Theory]`/`[DockerRequiredFact]`), ohne `bin/obj`. Sie sind
**konservative Untergrenzen** der ausgeführten Testfälle, weil Theorien mehrere Fälle je Attribut
erzeugen — nicht als absolute Gesamt-Testzahl zu lesen.

| Projekt | Zweck / Ebene | Quelldateien | Test-Attribute | TFM |
|---|---|---|---|---|
| `CalloraVoipSdk.Core.IntegrationTests` | Haupt-Suite L0–L3 (SIP/RTP/SRTP/SDP/ICE/RTCP, in-process gegen eigenen Stack + Fakes) | 289 | ~1398 | net8/9/10 |
| `CalloraVoipSdk.Client.Tests` | Client-/Facade-Schicht, Config-/Mapping-Drift-Guards | 22 | ~89 | net8/9/10 |
| `CalloraVoipSdk.Audio.Tests` | Audio-Adapter | 5 | ~10 | net8/9/10 |
| `CalloraVoipSdk.SoakTests` | Dauer-/Last-Läufe (`SoakShort`/`SoakLong`), Trend-Asserts | 20 | ~44 | net8/9/10 |
| `CalloraVoipSdk.InteropTests` | L4-Interop gegen echten Asterisk (Docker/Testcontainers) | 21 | ~38 | net8/9/10 |
| `CalloraVoipSdk.ArchitectureTests` | mechanische Engineering-Gates (siehe §4) | 2 | 7 | net8/9/10 |
| `CalloraVoipSdk.InteropHarness` | gemeinsames Fundament (Fixtures, Metrik-Sampler, Audit-Sink) — **kein Testträger** | 20 | 0 | net8/9/10 |

Grobe Kategorien der Abdeckung: das Schwergewicht liegt auf den **in-process-Integrationstests**
(L0–L3), die SIP-Dialog/Transaktion, RTP/RTCP, SRTP/DTLS-Krypto, SDP-Offer/Answer, ICE und
RTCP-Qualität tief abdecken. Darum liegen die separaten Schienen für **Soak** (Ressourcen-/
Qualitäts-Trend) und **Interop** (echter Fremd-Stack).

**Der `InteropHarness` ist das geteilte Fundament**, das von `SoakTests` und `InteropTests`
gemeinsam genutzt wird (statt Fixtures zu forken): Ebenen-Fixtures mit Fehlerinjektions-Hooks, ein
Metrik-Sampler (RAM/Handles/Threads/Sockets + RTCP), Szenario-Bausteine, Media-Verifier und ein
Audit-Sink (`Audit/SoakArtifactSink.cs`). Der Sampler **assertet auf Trends, nicht Momentaufnahmen**
(`Metrics/TrendAssertions.cs`, `NoUpwardDrift`) — ein Soak, der nur einen End-Zustand prüft, würde
einen langsamen Leak übersehen.

---

## 3. CI-Gates

Definiert in `.github/workflows/ci.yml` (PR + Push auf `main`, Matrix ubuntu/windows, .NET 8/9/10)
und `.github/workflows/soak.yml` (nightly).

Die Reihenfolge der Schritte im Haupt-Job ist bewusst:

1. **Restore + Build** — `Release`, `CodeAnalysisTreatWarningsAsErrors=true` (Warnungen brechen
   den Build).
2. **Architecture gates (ENGINEERING_RULES)** — die `ArchitectureTests` laufen als **eigener
   CI-Schritt VOR der eigentlichen Suite**. Verletzt ein Commit eine mechanische Regel, bricht der
   Lauf, bevor die teure Testsuite überhaupt startet.
3. **Test + Coverage (Non-Core)** — die Hauptsuite mit dem Filter
   `Category!=SoakLong & Category!=Interop & FullyQualifiedName!~ArchitectureTests` (Arch-Gate und
   die schweren Schienen sind hier ausgeschlossen) plus Coverage-Sammlung.

Ein **separater `interop`-Job** baut den Stack und fährt `Category=Interop` gegen dockerisiertes
Asterisk (`net10.0`). Der **nightly `soak`-Job** (`soak.yml`, cron `0 3 * * *`) fährt
`Category=SoakLong` mit konfigurierbaren `SOAK_ITERATIONS`/`SOAK_DURATION_SECONDS` und lädt die
Mess-Artefakte (JSON-Messreihen + `summary.md`) hoch.

Wirtschaftlichkeit: Public-Repo → Standard-Runner ohne Minuten-Kosten; Docker-Hub-Rate-Limits über
GHCR-Spiegelung umgangen. PR-CI bleibt schnell (Loopback + Kurz-Soak + Docker-Interop), die
langsame Abdeckung trägt der nightly-Lauf.

### Shrink-only-Baselines

Die mechanischen Regeln (§4) werden gegen eine **in den Test einkompilierte Baseline bekannter
Altlasten** geprüft. Die Mechanik (`SourceScan.AssertMatchesBaseline`):

- **Baselines dürfen nur schrumpfen.** Ein neuer Verstoß schlägt fehl.
- **Veraltete Baseline-Einträge schlagen ebenfalls fehl.** Wer eine Altlast behebt, muss den
  Eintrag aus der Baseline entfernen — sonst rot.
- Einträge sind repo-relative Dateipfade; die Begründung, warum ein Eintrag (noch) akzeptabel ist,
  steht als Kommentar direkt an der Baseline.

Der Nettoeffekt ist ein monotoner Ratchet: technische Schuld kann nur abgebaut, nie stillschweigend
angehäuft werden.

---

## 4. Durchsetzung der Engineering-Regeln

`ENGINEERING_RULES.md` ist die normative Regelbasis, auf die sich die `ArchitectureTests`
("Mechanische Gates für ENGINEERING_RULES.md") beziehen. Zwei Klassen:

### R1–R6 — mechanisch erzwungen (Gates)

Jede Regel wird über den gesamten Quellbaum geprüft und gegen die shrink-only-Baseline verglichen:

| Regel | Prüft | Baseline |
|---|---|---|
| **R1** | DDD-Schichtrichtung: `Domain` ohne `using` auf Application/Infrastructure/Client; `Application` ohne Infrastructure/Client | leer |
| **R2** | Namespace-Schichtsegment = Ordner-Schicht (keine Layer-Omission, kein Foreign-Layer) | leer |
| **R3** | max. 1000 Zeilen pro Datei (`src/`, `tests/`, Beispiele) | leer |
| **R4** | keine `private`/`protected` verschachtelten Typen in `src/` | leer |
| **R5** | kein stummer `catch` (leerer Body oder nur Kommentare) | 22 inventarisierte Altlasten (Dispose-/Transportpfade), nur schrumpfend |
| **R6** | kein `.GetAwaiter().GetResult()` (Sync-over-Async) in `src/` | 4 review-pflichtige Dispose-/Transport-Einträge |

Belegte Nuance zu R3: der Scanner adressiert `samples/`, das Verzeichnis heißt aber `examples/` —
Beispiele werden faktisch nicht von R3 erfasst (bekannte Kleinstlücke, dokumentiert in der
Tiefenanalyse 2026-07-22). Das ist als Limitation ausgewiesen, nicht als Vollabdeckung behauptet.

### K1–K8 — review-verbindlich (nicht mechanisch)

Diese Regeln sind durchgängig im Code dokumentiert und werden im Review erwartet; ein Verstoß gilt
als Fehler, auch wenn kein Gate ihn fängt:

- **K1 Fail-closed Medien-Sicherheit** — kein Klartext-Downgrade; bei fehlendem/ungültigem
  Schlüsselmaterial verwerfen bzw. ablehnen (488), nie unverschlüsselt senden/empfangen. Sends vor
  Schlüsselinstallation werden unterdrückt und gezählt, nicht gepuffert.
- **K2 Enricher-Reihenfolge** ist Invariante (ICE → SRTP → DTLS), Änderungen nur über
  `with`-Klone.
- **K3 Threading-Verträge** — synchroner Event-Dispatch mit Snapshot-im-Lock/Invoke-außerhalb;
  Medien-Hotpath ohne Locks über Fremdcode, bounded Buffer mit Drop-Oldest; atomare Snapshot-APIs;
  durchgängig `ConfigureAwait(false)`; idempotenter Dispose.
- **K4 Fehlerbehandlung an Vertrauensgrenzen** — Untrusted Wire-Input per `Try*`/null-Vertrag
  (Decode wirft nicht), malformte Pakete geloggt und verworfen; DoS-Kappen an jeder Wire-Grenze.
- **K5 Secrets** — Schlüsselmaterial nie in Logs (redigiert/geschwärzt); abgeleitete Keys bei
  Dispose genullt; Integritätsvergleiche konstantzeitig.
- **K6 Marker statt TODO** — `TODO`/`FIXME`/`HACK` unerwünscht; offene Punkte als strukturierte
  Follow-up-Prosa; behobene Findings tragen ihren Marker am Code (siehe §5).
- **K7 RFC-Verweise** — Protokollverhalten mit RFC-Nummer und Paragraph belegt; bewusste
  Abweichungen als solche markiert und begründet.
- **K8 Ebenenmodell** — Tests folgen L0–L4 (§1); Drift-Guards per Reflection
  (z. B. `VoipOptionsMappingCompletenessTests`) sind das bevorzugte Mittel gegen schleichende
  API-/Mapping-Erosion.

---

## 5. Audit- und Findings-Register-Disziplin

Der Zustand von Sicherheits-/Härtungs-Audits ist in-repo verankert statt in Transkripten
(ADR-057). Zwei git-getrackte Register unter der `!docs/audit/`-Ausnahme:

### Code-Findings-Register (`docs/audit/CODE_FINDINGS_REGISTER.md`)

Reverse-Index über alle im Code referenzierten Marker-Familien:

- **ADR** — Architekturentscheidungen
- **CF** — Code-Findings (Protokoll-Korrektheit, z. B. CF-001 Digest-Multi-Challenge RFC 8760)
- **HARD** — Härtungs-Findings (Sicherheit, Nebenläufigkeit, Ressourcen, Code-Qualität)
- **CORE** — Kern-Feature-/True-up-Marker
- **N** — NAT-Adressquellen im SIP-Kanal

Die Disziplin (K6): jedes behobene Finding trägt seinen Marker (`CF-…`/`HARD-…`/`ADR-…`) **direkt
am Code** (Kommentar/XML-Doc); das Register ist der Reverse-Index mit `Datei:Zeile`-Herkunft. Offene
Punkte sind strukturierte Follow-up-Prosa, kein `TODO`.

Zwei bewusst dokumentierte Ehrlichkeits-Punkte des Registers: (1) es ist eine **Rekonstruktion** —
das Original-Register lag außerhalb des Repos und überlebte den Public-Repository-Cut nicht; die
Beschreibungen geben den code-verankerten Stand wieder, nicht zwingend den originalen Wortlaut.
(2) **"Verify-before-claim":** ein Finding gilt erst als behoben, wenn es direkt gegen den Code
verifiziert ist — nicht wenn ein Review es behauptet. Der kanonische Fall ist der 2026-07-08-Overturn
zweier als *Critical* gemeldeter Findings (K1/K2), die eine Delta-Verifikation gegen den echten Code
widerlegte (der Reviewer hatte einen nicht existierenden Pfad zitiert und den Code nie gelesen).
Kritische Claims werden seither gegengeprüft, bevor sie in Status-Dokumente einfließen.

### Interop-/Soak-Register (`docs/audit/INTEROP_SOAK_AUDIT.md`)

Ein **"document-don't-fix"-Register**: die Interop/Soak-Audit-Läufe produzieren Befunde, aber
**kein autonomes Fixen** — jeder SDK-Fix ist ein separates, eigens freigegebenes Paket, sodass
Defekt und Fix getrennt review-bar bleiben. Jedes Finding trägt:

`FID` · Typ · Evidenz (Test/Peer) · Symptom · Fehlerquelle · `Datei:Zeile` · Fix-Vorschlag ·
Schweregrad · Status.

Finding-Typen: `Interop-Abweichung` · `Soak-Leak` · `Media-Defekt` · `Wire-Robustheit` ·
`Facade-Coupling-Gap`. Das Register hat bislang die Findings F001–F011 getrieben; ein Teil ist
inzwischen als separates Paket gefixt (u. a. F005/F006/F008/F009/F010/F011), einige bleiben
bewusst als dokumentierte Grenze offen (siehe §6). Das Register ist eine **Urteils-/
Dokumentationsschicht, kein Gate** — seine Findings brechen den Build nicht.

### Interop/Soak-Harness in Betrieb

- **Reale Interop ist bewiesen, nicht aspirational:** die Asterisk-Matrix
  (`andrius/asterisk:22` via Testcontainers) läuft laut ADR-058 mit "29 grün, 0 Skip"
  (Register/Call/Codec/SRTP-SDES/DTMF/Hold/Transfer/Session-Timer/Early-Media) plus eine
  bidirektionale Zwei-Bein-Media-Suite ("8 harte `[DockerRequiredFact]`, 0 Skip") mit byte-exakter
  Inhaltsverifikation in beide Richtungen.
- **Soak assertet auf Trends:** Ressourcen-Sockel bleiben flach über N Calls; Jitter/Loss driften
  nicht; Nebenläufigkeit zeigt keinen Deadlock/Race — gemessen vom Harness-Sampler.

---

## 6. Ehrlich: was NICHT (automatisiert/end-to-end) getestet ist

Die Register führen ihre eigenen Grenzen. Explizit ausgewiesen:

- **Browser-Interop (WebRTC gegen echte Browser)** ist nicht CI-verdrahtet. Der WebRTC-Peer ist
  signalisierungs-neutral entwickelt und in-process getestet; eine end-to-end-Validierung gegen
  Chrome/Firefox-Peers ist offen (ADR-009-Roadmap). Kein "production-ready"-Claim für Browser-Peer.
- **Akustische Audio-Qualität** wird **nicht** gemessen. Audio wird SDK-seitig via `IMediaSender`
  injiziert (kein Mikrofon, kein Codec-Encode) — die Tests messen den **Transport-/Medienpfad**,
  nicht MOS aus echtem Audio. Opus läuft transport-only (opake Payload).
- **MOS gegen Asterisk ist `null`:** Asterisk sendet kein RTCP-XR (RFC 3611), und der SDK berechnet
  keinen lokalen MOS-Schätzwert (E-Modell) — als Lücke notiert, kein Defekt.
- **Zeitgeraffte Langzeit-Signaling-Soaks** sind nicht möglich: der Signaling-Layer hat keine
  `ITimeProvider`-Abstraktion (Finding F003), Refresh-/Session-Timer nutzen hart `Task.Delay` →
  lange Signaling-Läufe sind nur real-time-beschleunigt, nicht echt zeitgerafft.
- **Live-RTT auf bare-L2** ist nicht messbar (F004): RTT ist eine L3-Orchestrator-Fähigkeit
  (`CallRtcpQualityMonitor`); im nackten L2-Media-Loopback bleibt sie ein statischer Anlauf-Hint.
  Über den vollen `VoipClient`/L4-Pfad wird sie real gemessen (belegt im Zwei-Bein-Media-Test:
  echte Peer-RTT befüllt).
- **Externe Peers 3CX/Fritzbox** sind opt-in/lokal, nicht CI — env-gated, Credentials via
  User-Secrets.
- **F011-DTMF-*Send* im early dialog** ist nur SDK-seitig nachgewiesen (`SendDtmfAsync` wirft im
  Ringing nicht + telephone-event verhandelt); der Peer-*Empfang* im early dialog ist NICHT
  end-to-end bestätigt.
- **Facade-Coupling-Gaps (F001):** die L0–L3-Fixtures umgehen die Facade und erfordern dafür
  `InternalsVisibleTo` — eine bewusst dokumentierte Kapselungs-Bruchstelle für den Test, kein
  öffentliches API-Seam.
- **Offene Media-Metrik-Präzision (F002):** eine lokale QoS-Delivery-Metrik
  (`PacketsUnrecoverableLoss`) überzählt Late-Drops auf reinem Loopback; der RTCP-Wire-Report-Pfad
  ist davon getrennt und korrekt. Als offen/adversarial-verifiziert registriert, nicht gefixt.

Der Kern-Signaling-/Medien-/Krypto-Pfad ist über L0–L4 tief und gegen einen echten Fremd-Stack
belegt. Die obigen Punkte sind die bewusst ausgewiesenen Grenzen — sie stehen im Register, nicht
unter einem grünen Lauf versteckt.
