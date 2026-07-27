# Architektur

> **Teil des technischen Due-Diligence-Pakets.** Stand: 2026-07-27.
>
> Diese Seite gibt einem technischen Käufer in einer Seite das Architekturbild: DDD-Schichtung,
> Modulkarte, tragende Subsysteme, Schicht-Regeln und deren maschinelle Durchsetzung. Alle
> Aussagen sind gegen den Quellbaum (`src/`) und die Architektur-Tests verifiziert; wo Prosa und
> Code auseinanderlaufen, gilt der Code (Prinzip „Doku ≤ Nachweis").

## 1. Überblick

CalloraVoipSdk ist ein kommerzielles .NET-VoIP-SDK. Der **Core** ist ein eigenständiger
SIP/RTP/SRTP-Stack: das Signaling, die Medien und die Medien-Sicherheit werden im Repository selbst
implementiert, es gibt **keine Laufzeitabhängigkeit auf einen externen SIP-Stack**. Fremdcode auf
dem kritischen Pfad beschränkt sich auf klar umrissene Kryptografie-/Codec-Bibliotheken
(BouncyCastle für DTLS/SHA-512-256, Concentus für Opus — siehe `../../adr/ADR-028-dtls-srtp-foundation.md`,
`../../adr/ADR-049-opus-codec-integration-concentus.md`).

Der Code ist nach **Domain-Driven Design** in vier Schichten geschnitten. Die Abhängigkeitsrichtung
läuft strikt von außen nach innen; `Infrastructure/*` ist internes Implementierungsdetail und kein
Teil der öffentlichen Vertragsfläche.

| Schicht | Verantwortung (1 Satz) | Ort im Baum |
|---|---|---|
| **Domain** | Entitäten, Value Objects, Zustände, Domain-Events und Domain-Ports (z. B. `ICallRegistry`) — reine Fachlogik ohne technische Abhängigkeiten. | `src/Core/Domain/` |
| **Application** | Use-Cases und Orchestrierung (`CallManager`, Media-Sessions) sowie die Ports (`Application/Ports/*`), über die die Infrastruktur angebunden wird. | `src/Core/Application/` |
| **Infrastructure** | Konkrete Protokoll-Adapter: SIP/SDP/RTP/RTCP/SRTP/DTLS/STUN/TURN/Audio — internes Implementierungsdetail. | `src/Core/Infrastructure/` |
| **SDK / Facade** | Öffentliche Konfigurations- und ICE-Oberfläche des Core-Projekts (`CalloraVoipSdk`-Namespace). Die zentrale Runtime-Facade `VoipClient`/`IVoipClient` liegt im Client-Projekt. | `src/Core/Sdk/`, `src/Client/Application/Facades/` |

Die Projektstruktur: `CalloraVoipSdk.Core` (Domain + Application + Infrastructure + Sdk),
`CalloraVoipSdk.Client` (Facade `VoipClient`, Manager, Module) und `CalloraVoipSdk` (öffentliche
WebRTC-/DI-/Hosting-Facade, Namespaces `CalloraVoipSdk.WebRtc` / `.DependencyInjection` /
`.Hosting`).

## 2. Modulkarte

### 2.1 Infrastructure-Module (`src/Core/Infrastructure/`)

Die dreizehn real vorhandenen Module (verifiziert per Verzeichnis-Scan):

| Modul | Verantwortung | Umfang¹ |
|---|---|---|
| **Sip** | SIP-Signaling: Wire-Codec, Transaktionen (Client/Server), Transport, Registrierung/Auth, Dialog-Routing, Adapter (`SipCoreCallChannel`, `SipLineChannel`), Observability. | 140 |
| **Rtp** | RTP-Medientransport: Sessions, Jitter-Buffer, BUNDLE-/Multi-Track-Transport, RTX, DTMF, Metriken, Video-Streams. | 89 |
| **Stun** | STUN-Wire/Messages/Attribute, Client-Seite **und** die send-seitige **ICE**-Maschinerie (`Stun/Ice/*`, Consent, Nomination). | 76 |
| **Turn** | TURN-Relay: Client-Control-Stack (Allocate/Permission/ChannelBind/Refresh) und Server-Hosting-Seite. | 73 |
| **Sdp** | SDP-Offer/Answer: Parsing/Serialisierung, `a=msid`, extmap-Verhandlung, BUNDLE-Gruppen. | 28 |
| **Common** | Protokoll-**agnostische** Querschnittsmuster (u. a. `Common/Relay`-Seams) — bewusst ohne protokollspezifische Logik. | 23 |
| **Dtls** | DTLS-SRTP-Keying-Fundament (RFC 5763/5764) samt Zertifikat/Media-Attachment. | 16 |
| **Srtp** | SRTP/SRTCP-Krypto-Kern und Session-Kontexte (Cipher, Key-Derivation). | 15 |
| **Media** | Medien-Pipelines/Formate/Routing als infrastrukturnahe Bausteine. | 13 |
| **WebRtc** | Core-seitige WebRTC-Engine (`WebRtcPeerConnection`) — der Motor hinter der öffentlichen Facade. | 7 |
| **Rtcp** | RTCP-Wire-Codec (Feedback/XR), toleranter Compound-Decode. | 3 |
| **Audio** | Infrastruktur-Anbindung für Audio-Adapter. | 2 |
| **Security** | Infrastruktur-nahe Sicherheits-Bausteine (z. B. `TlsConfiguration`). | 2 |

¹ Anzahl `*.cs`-Dateien im Modul (2026-07-27) als grobes Größenmaß, nicht als Feature-Aussage.

> **Abweichung zur häufigen Annahme (verifiziert):** Es gibt **kein** top-level Modul
> `Infrastructure/Ice`. ICE ist auf `Infrastructure/Stun/Ice/*` (send-seitige State-Machine,
> RFC 8445) und `Application/Media/Ice/*` (Kandidaten-Modell) verteilt.
> Ein top-level `Infrastructure/Hosting` existiert ebenfalls nicht — die TURN/STUN-**Server-Hosting**-Facade
> liegt im Client-Projekt unter `src/Client/Hosting/` (Namespace `CalloraVoipSdk.Hosting`).
> Der aspirationale Baum in `../../Projektbaum.md` (Stand April) beschreibt eine Zielstruktur und
> nicht durchgängig den heutigen Stand.

### 2.2 Application, Domain und Client

| Bereich | Verantwortung |
|---|---|
| **Domain** (`src/Core/Domain/`) | `Calls`, `Lines`, `Messages`, `Publications`, `Events`, `Security` — Fachkern plus Domain-Ports (`ICallRegistry`). |
| **Application** (`src/Core/Application/`) | Use-Cases (`Calls`, `Lines`, `Media`, `Convenience`) und die Ports `Application/Ports/{Audio,Connectivity,Media,Sdp,Video}`, die die Infrastruktur als austauschbare Adapter anbinden. |
| **Client** (`src/Client/`) | Öffentliche Runtime-Facade `VoipClient`/`IVoipClient` (`Application/Facades`), Manager, Module und Workflows sowie die Hosting- und WebRtc-Anbindung. |

## 3. Schicht-Regeln und ihre Durchsetzung

Die verbindlichen Regeln stehen in `../../../ENGINEERING_RULES.md`. **R1–R6 sind mechanisch
erzwungen**; K1–K8 sind verbindliche Konventionen, die im Review geprüft werden.

| Regel | Inhalt | Baseline / Zustand |
|---|---|---|
| **R1 — Schichtrichtung** | `Domain` hat kein `using` auf `Application`/`Infrastructure`/`Client`; `Application` keins auf `Infrastructure`/`Client`. Braucht die Domain etwas von außen, definiert sie einen eigenen Port (Beispiel: `ICallRegistry`, implementiert vom Application-`CallManager`, siehe `../../adr/ADR-015-icallregistry-domain-port-dip.md`). | **leer** — null Verstöße. |
| **R2 — Namespace = Ordner-Schicht** | Eine Datei unter `Domain/`/`Application/`/`Infrastructure/` trägt ihr eigenes Schichtsegment im Namespace und kein fremdes (keine Layer-Omission, kein Foreign-Layer). | **leer**. |
| **R3 — ≤ 1000 Zeilen/Datei** | Übergroße Dateien werden in Kollaborator-Klassen zerlegt. | **leer**. |
| **R4 — keine private/protected nested Types** | Hilfstypen werden Top-Level-`internal` in eigener Datei. | **leer**. |
| **R5 — kein stummer `catch`** | Jeder `catch` loggt oder behandelt sichtbar. | Baseline von 22 inventarisierten Altlasten, **shrink-only**. |
| **R6 — kein Sync-over-Async** | `.GetAwaiter().GetResult()` in `src/` verboten außer inventarisierten Dispose-/Transportpfaden. | Baseline von 4 Einträgen, review-pflichtig. |

**Wie erzwungen (der Mechanismus, nicht nur die Absicht):**

- **Architektur-Tests als Gate.** `tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs`
  scannt den Quellbaum (`src/Core/{Domain,Application,Infrastructure}` für R1/R2, alle drei plus
  `src/` für die übrigen) per `using`-/Muster-Scan und vergleicht gegen eine im Test einkompilierte
  **Baseline bekannter Altlasten** (`SourceScan.AssertMatchesBaseline`).
- **Shrink-only-Ratsche.** Beide Drift-Richtungen schlagen fehl: ein **neuer** Verstoß **und** ein
  **veralteter** Baseline-Eintrag (behobene Altlast muss aus der Baseline entfernt werden). Damit
  wird eine dokumentierte Regel zu einem monoton fallenden Schuldenstand statt zu Prosa.
- **Gate läuft vor der Suite.** In `.github/workflows/ci.yml` laufen die Architektur-Gates als
  eigener Schritt **vor** den Verhaltenstests — eine Schichtverletzung schlägt schnell und
  unabhängig fehl.
- **Grenze der Prüfung (ehrlich benannt):** Der R1-Scan ist ein `using`-Regex, keine volle
  semantische Analyse — er fängt Namespace-Leaks, nicht z. B. Reflection-Kopplung. R3 scannt
  historisch `samples/`, während das reale Beispiel-Verzeichnis `examples/` heißt; Beispiele werden
  faktisch nicht erfasst (bekannte Kleinstlücke, dokumentiert in `../../../ENGINEERING_RULES.md`).

## 4. Tragende Architektur-ADRs

Der vollständige, verifizierte ADR-Index liegt unter `../../adr/README.md`. Die für Schichtung und
Delivery-Prozess tragenden Entscheidungen:

| ADR | Entscheidung |
|---|---|
| [`ADR-013`](../../adr/ADR-013-role-based-delivery-workflow.md) | Rollenbasierter Delivery-Workflow (CEO / PO / DEV / Reviewer). |
| [`ADR-014`](../../adr/ADR-014-ddd-layering-gated-baselines.md) | DDD-Schichtrichtung durch gated, shrink-only Baselines maschinell erzwungen (Fundament dieser Seite). |
| [`ADR-015`](../../adr/ADR-015-icallregistry-domain-port-dip.md) | `PhoneLine`↔`CallManager`-Entkopplung über den Domain-Port `ICallRegistry` (Dependency Inversion) — der Fix, der die Layering-Baseline auf leer brachte. |
| [`ADR-016`](../../adr/ADR-016-peer-calibrated-refactoring-backlog.md) | Peer-kalibriertes Refactoring-Backlog: härten statt neu bauen. |
| [`ADR-057`](../../adr/ADR-057-audit-findings-register-marker-discipline.md) | Audit → Findings-Register → Code-Marker als belegbares Audit-Gedächtnis. |
| [`ADR-058`](../../adr/ADR-058-layered-test-interop-soak-model.md) | Geschichtetes L0–L4-Testmodell mit Interop/Soak-Harness. |

Die protokollspezifischen Entscheidungen (SIP, SDP, SRTP/DTLS, RTP/RTCP, ICE, Video, WebRTC/TURN)
sind je Subsystem in `../../adr/README.md` gruppiert; verwandte Referenzdokumente liegen unter
`../../reference/README.md`.
