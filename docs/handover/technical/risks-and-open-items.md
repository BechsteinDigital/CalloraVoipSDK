# Risiken und offene Punkte — Register

*Teil des technischen Due-Diligence-Pakets.*
Stand: 2026-07-27 · Code-Basis: `main` (Media-/SIP-Kern) + WebRTC/BUNDLE-/TURN-Track · verifiziert gegen ADR-Verzeichnis, Findings-Register und Code-Graph.

## Zweck und Lesart

Diese Seite ist das **ehrliche Risiko- und Offene-Punkte-Register** für den Käufer. Sie ist bewusst
unbequem: Ziel ist nicht, das Produkt gut aussehen zu lassen, sondern jeden bekannten Deferral,
jede Nachweislücke und jede Doku-Drift so zu benennen, dass eine Kaufentscheidung auf belastbarem
Stand getroffen werden kann.

**Grundregel „Doku ≤ Nachweis":** Jedes Risiko ist mit einer nachprüfbaren Quelle belegt (ADR,
Roh-Register, Code:Zeile). Es werden keine Risiken erfunden — aber auch keine verschwiegen. Wo ein
Punkt bereits behoben ist, steht er nicht hier (sondern im jeweiligen Register als „gefixt").

**Schwere-Skala** (begründet je Zeile):

| Schwere | Bedeutung |
|---------|-----------|
| **Hoch** | Blockiert einen produktiven Einsatzpfad oder ein Sicherheitsversprechen; vor GA/Produktion adressieren. |
| **Mittel** | Funktions-/DX-/Interop-Einschränkung mit Workaround oder begrenztem Wirkbereich. |
| **Niedrig** | Kosmetik, Beobachtbarkeit, oder Nachweislücke ohne Verhaltensrisiko. |

**Wichtiger Vorbehalt zur Reife:** „Im Code umgesetzt/belegt" ist **nicht** dasselbe wie „gegen einen
realen Referenz-Stack verifiziert". Der WebRTC-/TURN-/BUNDLE-Track ist überwiegend gegen **Fake-Server
über Loopback** getestet; es existiert **keine Browser-Interop-Validierung** (siehe
[ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md)). Der SIP-/Audio-Kern ist dagegen gegen
einen echten **Asterisk** interop-getestet (siehe [ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md)
und das Interop-/Soak-Register).

**Primärquellen:**
- Interop-/Soak-Fehlerregister: [`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md) (F001–F011)
- Code-Findings-Register (Marker → Code:Zeile, getrackter Paket-Beleg): [`../../audit/CODE_FINDINGS_REGISTER.md`](../../audit/CODE_FINDINGS_REGISTER.md)
- Audit-Hardening-Backlog (Pakete A–I) — intern, nicht Teil des Pakets (auf Anfrage/NDA); der öffentliche Beleg ist das [Code-Findings-Register](../../audit/CODE_FINDINGS_REGISTER.md).
- ADR-Verzeichnis (inkl. Errata + „honest open edges"): [`../../adr/README.md`](../../adr/README.md)
- Archivierte Engineering-Logs — intern, nicht Teil des Pakets (auf Anfrage/NDA).

---

## 1. Sicherheits- und Media-Hardening-Deferrals

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| SDES-Key über unsichere Signalisierung | Default (`SrtpPolicy.Optional`, `OfferDtlsSrtp=false`, UDP) bietet SRTP per SDES an → Master-Key reist als `a=crypto` im **Klartext-SDP**. Gegen passiven Mitleser auf ungesicherter Signalisierung wirkungslos. Warnung + opt-in-Enforce (`RequireSecureSignalingForSdes`) sind gebaut, aber der **Default warnt/enforced nicht hart** (bewusst, um SDES-only-Interop nicht zu brechen). | **Mittel** | Interop-Register F007 (gefixt = Warnung/Enforce opt-in); `SipCoreCallChannel.cs:188`; RFC 4568 §7 | Für Produktionsdeployments TLS/SIPS-Signalisierung + `RequireSecureSignalingForSdes`-Flag setzen, oder DTLS-SRTP bevorzugen wo der Peer es kann. Käufer über Default-Semantik aufklären. |
| WebRTC Send-vs-Dispose Media-Hotpath (C6-Rest) | Voller Thread-Safety-Vertrag `SendAudioAsync`/`SendVideoFrameAsync` vs. `DisposeAsync` war zum Backlog-Zeitpunkt als Deferred markiert; inzwischen via `SendDrainGate` geschlossen — aber der interim-Threading-Vertrag verlangt weiterhin **geordnete Single-Caller-Signalisierung** (`_sync` schützt nur Felder, serialisiert nicht ungeordnete Concurrent-Signalisierung). | **Niedrig–Mittel** | AUDIT_HARDENING_BACKLOG HARD-C6; Findings HARD-C6 (`SendDrainGate.cs`, `WebRtcPeerConnection.cs:30`) | Signalisierungs-Handshake als eine geordnete Sequenz eines Callers halten (W3C-Signalling-State). Bei öffentlicher Fassaden-Nutzung im Vertrag dokumentieren. |
| Ephemere DTLS-Identität als Default | Persistente/gepinnte DTLS-Identität ist opt-in gebaut (`DtlsCertificate.FromX509`, ECDSA P-256, fail-closed), aber Default bleibt ephemer. RSA und HSM/CNG-Keys werden **fail-closed abgelehnt** (nicht unterstützt). | **Niedrig** | Findings HARD-E7 (`DtlsCertificate.cs:72`); RFC 5763/8122 | Für HSM-/PKI-Nischen ist eine RSA-/Signer-Abstraktion Folgearbeit. Für WebRTC-Standardfall unkritisch (fingerprint-auth). |
| RTP Send-/Concealment-Pooling | Hotpath alloziert pro Paket kleine, entkommende Objekte (RtpPacket/Datagramm/Concealment); Pooling ist **gated auf Profiling-Signal** — kein bolt-on möglich (Use-after-return-Korruption). | **Niedrig** | AUDIT_HARDENING_BACKLOG HARD-F1-Rest/HARD-F3 (GATED); ADR-032/052 | Erst unter realer Multi-Call-Last profilen; wenn GC-Druck belegt → durchgängige pooled/ref-counted Media-Buffer-Pipeline (Design-Item, kein Quick-Win). Peers (SIPSorcery) deferrieren dasselbe. |

---

## 2. Interop- und Browser-Nachweislücken

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| Keine Browser-Interop-Validierung | Der gesamte WebRTC-/BUNDLE-/TURN-Track hat **keinen** verifizierten Lauf gegen Chrome/Firefox. „Production-ready" darf für WebRTC **nicht** behauptet werden, bis ein End-to-End-Browser-Lauf existiert. | **Hoch** | [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) (Guardrail); [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); AUDIT_HARDENING_BACKLOG HARD-H8 / B6 | Browser-Interop-Harness bauen (echter Browser-Peer, Trickle-ICE, Candidate-Ordering). Bis dahin WebRTC als „transport-vollständig, interop-ungetestet" führen. |
| TURN-Relay nur gegen Fake-Server | Der komplette TURN-Relay-Datenpfad (Allocation/Permission/ChannelBind/Keepalive/Whole-Socket-Transition) ist gegen einen **Fake-TURN-Server über Loopback** getestet; **kein** Lauf gegen einen echten TURN-Server (4d-6). Ganze Session-Relay-Nominierung ist timeout-bound (~10 s Direct-Exhaustion) → nicht im Fast-Unit-Test. | **Hoch** | [ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md), [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md), [ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md) (jeweils „no real-server end-to-end run") | E2E gegen echten `coturn`/TURN-Server aufsetzen (4d-6). Priorität für jeden NAT-durchquerenden Produktiveinsatz. |
| SIP-Interop nur gegen Asterisk | Der SIP-/Audio-Kern ist gegen echten Asterisk (`andrius/asterisk:22`) interop-getestet (29 grün, 2-Bein-Media bidirektional). **FreeSWITCH/3CX/Fritzbox** sind geplant, aber noch nicht als grüne Suite belegt. | **Mittel** | Interop-Register Coverage-Notizen; [ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md); Memory-Anchor „Interop+Soak+Audit" | Interop-Matrix auf weitere PBX/Endpunkte ausweiten. Asterisk-Nachweis ist substantiell, aber nicht repräsentativ für alle Peers. |
| DTMF-Send im Early-Dialog nur SDK-seitig | Der DTMF-*Send* im early dialog ist nur SDK-seitig nachgewiesen (`SendDtmfAsync` wirft nicht + telephone-event verhandelt); Peer-*Empfang* und der zur Laufzeit genommene Sendepfad (RTP-telephone-event vs. SIP-INFO-Fallback) sind **nicht** end-to-end bestätigt. | **Niedrig–Mittel** | Interop-Register F011 (Coverage-Notiz „Vorbehalt F011-DTMF") | End-to-End-DTMF-Empfang im early dialog gegen realen Peer verifizieren. |
| Lokaler MOS-Schätzwert fehlt | `RemoteMosListeningQuality`/`RemoteMosConversationalQuality` bleiben `null`, wenn der Peer kein RTCP-XR (RFC 3611) sendet (z. B. Asterisk); der SDK berechnet **keinen** lokalen E-Modell-MOS. | **Niedrig** | Interop-Register Coverage-Notiz Zwei-Bein-Media | Optionales SDK-Feature: lokale MOS-Schätzung (E-Modell). Kein Defekt, aber Käufer-Erwartung ggf. abgleichen. |

---

## 3. TURN-Relay-Grenzen

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| Controlled-Agent-Relay-Gap | Nur der **controlling** Agent treibt Nominierung und installiert Relay-Permissions. Offerer-relay ↔ Answerer-direct funktioniert; **Answerer-owning-the-relay bei direktem Offerer nicht** — die controlled Seite triggert weder eigene Relay-Nominierung noch Permission. Braucht Design, ist nicht gebaut. | **Mittel** | [ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md) („Controlled-agent relay gap"); [ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md) (Carry-over) | Design für controlled-Agent-Relay (Nomination-Trigger + Permission-Install auf der Answerer-Seite). Bis dahin Relay nur asymmetrisch nutzbar. |
| TCP/TLS-Relay-Datenpfad fehlt | Der TURN-Stack ist **UDP-Relay**. Der TCP/TLS-*Control*-Pfad ist separat bewiesen, aber ein persistenter **Stream-Relay-Datenpfad** ist ein großes, ungebautes Feature (WebRTC `TurnAllocationProbe` ist UDP-Socket-gebunden). | **Mittel** | [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md), [ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md) („out of scope"); Memory-Anchor WebRTC-Roadmap | Nur relevant, wenn UDP-TURN im Zielnetz blockiert ist. Als Design-gated-Feature führen. |
| Relay→Direct-Downgrade nicht unterstützt | Nach Commit auf Relay (`SetRelayChannel`) transitioniert der Transport **nicht zurück** zu Direct. In der Praxis misrouten Direct-Checks nach dem Flip (Sub-Sekunden-Fenster geschlossen), aber ein formaler Downgrade ist nicht implementiert. | **Niedrig** | [ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md) („Relay→direct re-nomination after commit is closed") | Akzeptabel für Steady-State-Relay. Bei Bedarf formaler Downgrade als Folgearbeit. |
| Multi-TURN-Adoption partiell | Nur die **erste** erfolgreiche Allocation wird für Adoption behalten; weitere konfigurierte TURN-Server emittieren zwar einen Kandidaten, werden aber nicht für Relay adoptiert. | **Niedrig** | [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md) („Multi-TURN adoption is partial") | Multi-TURN-Failover als Folgearbeit, wenn Redundanz gefordert. |
| Dispose-Latenz unter totem Relay | Teardown-`Refresh(0)` ist durch `_teardownTimeout` (~2 s) begrenzt; gegen einen unerreichbaren Relay kann Disposal bis zu diesem Bound dauern. | **Niedrig** | [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md) („Dispose latency under a dead relay") | Bounded und dokumentiert; ggf. Timeout konfigurierbar machen. |

---

## 4. ICE-SIP-Pfad-Unverdrahtungen (built-but-unwired)

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| ICE-Consent-Freshness auf SIP-Pfad unverdrahtet | `IceConsentMonitor`/`IceConsentFreshnessPolicy` (RFC 7675, 30 s Expiry) sind gebaut+getestet, aber **kein SIP-Pfad-Caller startet den Monitor nach Nominierung**. Ein nominiertes SIP-Paar wird von diesem Code **nicht** am Leben gehalten oder bei Consent-Verlust abgerissen. Consent ist nur auf dem Bundle/WebRTC-Pfad live. | **Mittel** | [ADR-041](../../adr/ADR-041-consent-freshness-and-ice-restart-primitives.md) (Consequences: „Neither primitive runs on the SIP path") | Bei Bedarf für SIP-ICE-Calls: `CallIceAgent` stateful + `IAsyncDisposable` machen, Monitor nach Nominierung starten (~200–300 Z., Media-Hotpath, fresh-context-Paket). Doku darf SIP-Consent-Freshness **nicht** behaupten. |
| ICE-Restart auf SIP-Pfad unverdrahtet | `IceRestartDetector.IsRestart` (RFC 8445 §9.1.1.1) ist eine reine, getestete Funktion, wird aber von **keinem** Produktionstyp konsumiert; der geplante `IIceRestartCoordinator`-Port existiert nicht. Consent-Loss-Reaktion (terminate vs. restart) ist **founder-undecided**. | **Mittel** | [ADR-041](../../adr/ADR-041-consent-freshness-and-ice-restart-primitives.md) („Restart wiring: absent everywhere") | Founder-Entscheidung zur Consent-Loss-Reaktion einholen, dann `IIceRestartCoordinator` verdrahten. |
| ICE auf SIP-Pfad ist send-side / one-shot | Der SIP-`CallIceAgent` ist stateless, liefert ein One-Shot-`CallIceSelectionResult`, hält kein nominiertes Paar. Full-ICE (Connectivity-Checks über alle Kandidatenpaare, srflx/relay/NAT) über den SIP-Pfad ist Backlog-Item, nicht die live Full-ICE-Maschinerie des Bundle-Pfads. | **Mittel** | [ADR-040](../../adr/ADR-040-send-side-ice-state-machine.md), [ADR-042](../../adr/ADR-042-ice-verification-and-shared-socket-gathering.md); AUDIT_HARDENING_BACKLOG HARD-H2 | Für NAT-schwere SIP-Szenarien Full-ICE auf dem SIP-Pfad bewerten; Bundle-Pfad hat die reifere ICE-Implementierung. |
| R5-Follow-up: Full-Path-ICE-Test fehlt | Der `with`-Erhalt der Keying/Video-Parameter nach ICE-Auswahl ist auf dem Typ getestet, aber ein Full-Path-ICE+Secure-Media-Test mit Fake-`ICallIceAgent` steht aus, bis der ICE+Secure-Media-Pfad live ist. | **Niedrig** | Findings HARD-R5; AUDIT_HARDENING_BACKLOG Register (R5-Follow-up) | Test nachziehen, wenn SIP-ICE+Secure-Media end-to-end verdrahtet wird. |

---

## 5. TWCC- / BUNDLE-Verdrahtung

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| Transport-CC nicht über den BUNDLE | Transport-wide-CC (Feedback-Plane + Estimator + Recommended-Bitrate) ist **live auf dem SIP-Video-Pfad** (`SipCoreCallChannel.cs:774`, opportunistisch), aber die Verdrahtung **über den geteilten BUNDLE-Transport** ist offenes Backlog-Item (Stamper bereit, Counter geteilt zu bauen). | **Mittel** | AUDIT_HARDENING_BACKLOG HARD-H3 („transport-cc über den Bundle"); [ADR-038](../../adr/ADR-038-transport-cc-feedback-plane.md) | Geteilten transport-wide Counter im BUNDLE-Transport verdrahten, wenn BUNDLE-Congestion-Control gefordert. |
| TWCC ist sender-side / video-only / single-stream | Der Estimator treibt nur den **ausgehenden Video**-Bitrate (Audio hat kein transport-cc-Stamping); single-stream. Recommendation ist **nicht** in `CallQualitySnapshot`, sondern via `ICall.SetVideoCongestion`/Event. | **Niedrig–Mittel** | [ADR-039](../../adr/ADR-039-transport-cc-estimator-bitrate-api.md) („Sender-side only", „NOT in CallQualitySnapshot") | Käufer über den Wirkbereich (nur Outbound-Video) aufklären; kein Regelkreis für Inbound/Audio. |
| TWCC nutzt Draft-URI, nicht RFC 8888 URN | Der Resolver matcht die libwebrtc-Draft-URI, nicht die registrierte RFC-8888-URN. Interop heute mit Chrome/libwebrtc-Stil-Peers. RTX-Pakete nehmen nicht am transport-wide seq teil; Encoder ist nicht compact (kein Run-Length; Encode wirft bei Receive-Gap > ~8,19 s). | **Niedrig** | [ADR-038](../../adr/ADR-038-transport-cc-feedback-plane.md) (Honest limits) | RFC-8888-URN + compact Encoder + Window-Splitting als Folgearbeit; für Chrome-Interop unkritisch. |
| TWCC-Tuning un-kalibriert / keine Hysterese | α/Threshold/AIMD-Step sind vernünftige Defaults, aber **nicht netz-kalibriert**; Fixed-Threshold-Signal kann am Rand oszillieren (EWMA glättet, eliminiert nicht). | **Niedrig** | [ADR-039](../../adr/ADR-039-transport-cc-estimator-bitrate-api.md) („un-calibrated", „No hysteresis") | Kalibrierung + adaptive-threshold/SCReAM-Upgrade (Seam vorhanden) als Folgearbeit. |
| Malformed TWCC-Feedback verwirft ganzes Compound | Ein fehlerhaftes Inbound-Feedback wirft `ArgumentException` und verwirft das ganze RTCP-Compound (vorbestehendes Verhalten aller Feedback-Decoder). | **Niedrig** | [ADR-038](../../adr/ADR-038-transport-cc-feedback-plane.md) („discards the whole compound") | Fault-tolerantes per-Type-Decoding als Folgearbeit. |

---

## 6. API-Gate-Drift (ADR-006 Errata)

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| Automatisches Public-API-Surface-Gate fehlt | ADR-006 §4 behauptete ursprünglich ein automatisches `PublicApiSurfaceTests`-Diff gegen `PublicApi.approved.txt`. **Verifiziert 2026-07-27: nie gebaut** — weder Test noch Baseline noch `CalloraVoipSdk.Core.Tests`-Projekt existieren. Public-API-Änderungen werden heute **nur** durch Review + `[Obsolete]`-Disziplin + CHANGELOG regiert, nicht durch einen automatischen Surface-Gate. `ArchitectureTests` prüft Engineering-Rules (Layering/Dateigröße/silent-catch), **kein** API-Diff. | **Mittel** | [ADR-006](../../adr/ADR-006-api-versioning-strategy.md) §4 + Errata (2026-07-27) | Echtes Public-API-Surface-Gate bauen (offene Folgearbeit). Bis dahin ist API-Drift nur durch menschliches Review gefangen — Risiko für ungewollte Breaking-Changes. |
| ADR-011 B4/B5-Wording-Drift | Ursprüngliche Slice-Prosa divergiert von der Implementierung: „Video als Track" ist ein **neuer** `BundledVideoTrack` (nicht die Konversion von `VideoRtpStream`, das bewusst als Non-BUNDLE-Pfad bleibt); BUNDLE-SDP-Generierung lebt auf `SdpOfferAnswerNegotiator`, nicht am genannten SIP-Feld (existiert nicht). Entscheidung unverändert, nur Wording lief dem Code hinterher. | **Niedrig** | [ADR-011](../../adr/ADR-011-rtp-multitrack-transport.md) Errata (2026-07-27) | Reine Doku-Drift, im ADR bereits korrigiert. Kein Code-Risiko. |
| ADR-038 C10-Logs stale (liveness) | Alle C10-Logs sagen „gate-off / not offered / inert"; **aktueller Code offeriert die extmap live** (nur peer-gated). Die Logs dokumentieren den Build-up, nicht den Live-Stand. | **Niedrig** | [ADR-038](../../adr/ADR-038-transport-cc-feedback-plane.md) („The C10 logs are stale on liveness") | Reine Doku-Drift, im ADR korrigiert. Beim Lesen archivierter Logs beachten. |

---

## 7. Test-Lücken (Interop-/Soak-Register F001–F011)

Status je Finding aus [`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md). Die meisten Facade-Defekte
(F005/F008/F009/F010/F011) sind **gefixt**; hier stehen die **offenen** Rest-Risiken und die als „dokumentiert, kein Fix"
geführten Coupling-Gaps.

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| F002 — falsche Unrecoverable-Loss-Metrik | Auf reinem UDP-Loopback (0 echter Verlust) zählt `RtpCallMediaSession` Late-Drops fälschlich als `PacketsUnrecoverableLoss` (Doppel-Zählung: Late-Paket IST angekommen). **Nur lokale QoS-Delivery-Metrik verfälscht**; RTCP-Wire-Report (`CumulativePacketsLost`) bleibt korrekt, keine Datenpfad-Korruption. Der `PacketsUnrecoverableLoss==0`-Soak-Assert bleibt F002-blockiert (Skip). | **Niedrig–Mittel** | Interop-Register F002 (adversarial CONFIRMED); `RtpCallMediaSession.cs:540-558`, Late-Handler @660 | Cursor `_lastDeliveredSequence` im Late-Handler extended-seq-aware vorrücken **und** Late nicht zu UnrecoverableLoss beitragen lassen (Warnung: naives Vorrücken maskiert echten Out-of-Order-Loss). |
| F001 — L0–L3-Typen sind `internal` | Sub-Facade-Komponenten (`RtpCallMediaSession` u. a.) sind `internal`; Test unter der Facade erfordert `InternalsVisibleTo`. Bewusste Kapselung, kein Defekt. | **Niedrig (Info)** | Interop-Register F001 (dokumentiert); `RtpCallMediaSession.cs:22` | Bewerten, ob ein schmales öffentliches Test-/Diagnose-Seam sinnvoll ist. |
| F003 — keine Zeit-Abstraktion im Signaling | `SipLineChannel`-Refresh-Loop und `SipSessionTimerManager` nutzen hart `Task.Delay`; 100+-Zyklen-Soaks bräuchten reale Stunden (kein echtes Zeit-Rafting). Testbarkeits-Grenze, kein Laufzeit-Defekt. | **Niedrig (Info)** | Interop-Register F003 (dokumentiert) | Optionaler `ITimeProvider`-Seam (~60 Z., 2 Injektionspunkte) für echte Signaling-Soaks. |
| F004 — RTT an bare-L2 nur statischer Hint | Der bare `RtpCallMediaSession` verdrahtet keinen RTCP-Quality-Monitor → `RoundTripTimeMs` bleibt der Anlauf-Hint. **An L3/L4 (`CallMediaOrchestrator`/`VoipClient`) wird RTT real gemessen** (gegen Asterisk verifiziert). Schicht-Grenze, kein Defekt. | **Niedrig (Info)** | Interop-Register F004 + Coverage-Notiz Zwei-Bein-Media | RTT-Assertions gehören auf L3+, nicht in den L2-Loopback. |
| F005b — Auth-Fehler-Durchreichung | (Gefixt) `ConnectResult.Error` liefert jetzt die Auth-Fehlerursache bei terminalem `Failed`. Als geschlossen geführt. | **Niedrig (gefixt)** | Interop-Register F005b (GEFIXT) | Keine — Nachweis vorhanden. |

*Nicht offen (gefixt, hier nur zur Vollständigkeit):* F005 (Connect-Short-Circuit), F006 (INVITE-Auth-Retry 481 — **war Hoch**, blockierte alle authentifizierten ausgehenden Calls, gefixt), F007 (SDES-Warnung/Enforce), F008 (Dial-ConnectTimeout), F009 (Cancel→Canceled), F010 (TCP/TLS-Register hinter NAT), F011 (Early Media).

---

## 8. Doku-Drift-Rest und veraltete Status-Docs

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| ADR-Volltexte teils backfilled | ADR-013…061 wurden am 2026-07-27 aus 114 archivierten Logs + Git-History **rückwirkend** verfasst und gegen den Code verifiziert. Wo ein Log mehr behauptete als der Code liefert, hält der ADR den realen Stand — aber Käufer sollten ADRs als *rekonstruiert*, nicht als *zeitgleich-authored* lesen. | **Niedrig** | [ADR-README](../../adr/README.md) („backfilled on 2026-07-27… verified against the actual source") | Als bekannt-rekonstruiert führen; die Errata-Disziplin (006/011/038) ist der Beleg für Ehrlichkeit. |
| CODE_FINDINGS_REGISTER ist Rekonstruktion | Das Original-Marker-Register lag **außerhalb** des Repos und ist nicht überliefert; die Beschreibungen sind **ausschließlich aus Code-/Kommentar-Kontext** abgeleitet — nicht zwingend der volle Original-Wortlaut. | **Niedrig** | [CODE_FINDINGS_REGISTER](../../audit/CODE_FINDINGS_REGISTER.md) §Zweck+Herkunft | Register als code-verankert (nicht kanonisch) lesen; Marker im Code sind die Source of Truth. |
| STATE.json / Backlog-Stände divergieren im Datum | AUDIT_HARDENING_BACKLOG trägt Stand 2026-07-16; einige Items (C6, E3, Sweep-Findings) wurden später neubewertet/korrigiert (E3 „war-schon", E8 False-Positive). Interne Status-Docs (`docs/archive/status-raw/SESSION_HANDOFF.md`, STATE.json, TODO/RFC-Docs) sind teils gitignored und nicht Teil des Käuferpakets. | **Niedrig** | AUDIT_HARDENING_BACKLOG (Datumsdrift, E3/E8-Korrekturen); CLAUDE.md Source-of-Truth-Regeln | Für Due-Diligence die *verifizierten* Register (dieses Paket + ADRs + Findings) nutzen, nicht die internen gitignored-Arbeitsdocs. |
| „gitignored" Durability-Docs (ADR-010/011) | ADR-010/011-Durability war laut Backlog G5 zu klären (gitignored). Für den Käufer relevant: die BUNDLE-Entscheidungen sind in den ADRs kanonisiert, die Arbeits-Docs nicht. | **Niedrig** | AUDIT_HARDENING_BACKLOG HARD-G5 | Keine offene Code-Arbeit; ADR-010/011 sind maßgeblich. |

---

## 9. Verbleibende Roadmap-Posten (nicht gebaut, bewusst deferred)

| Thema | Risiko / Offener Punkt | Schwere | Beleg | Empfohlene Maßnahme |
|-------|------------------------|---------|-------|---------------------|
| Native Video-Codecs (VP8/H264 Encode/Decode) | Video ist **transport-only**: es existieren nur Depacketiser/Packetiser (`Vp8Depacketiser`, `H264Depacketiser`, `AnnexBParser`), **kein** nativer Encoder/Decoder. Bewusste Produktentscheidung (Codec transport-only). | **Mittel** | Code-Graph (Packetisation-Modul, keine Encoder-Typen); Memory-Anchor „Video-Interop & Codec-Entscheidung"; [ADR-043](../../adr/ADR-043-video-sdp-negotiation.md)…048 | Für „SFU/Transport"-Szenarien ausreichend. Käufer über fehlenden nativen Encode/Decode aufklären; App/Peer liefert Encoding. |
| SCTP-DataChannels | WebRTC-DataChannels (SCTP) sind ein **späterer, ungebauter** Roadmap-Slice; blockieren den Medien-MVP nicht. | **Mittel** | [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) §7 (SCTP, deferred); Memory-Anchor WebRTC-Roadmap | Als geplant-nicht-gebaut führen. Nur relevant, wenn DataChannel-Feature gefordert. |
| Recv-side Simulcast-Demux | Simulcast ist **send-side-only** (App besitzt Encoder, SDK packetisiert per rid). Kein „simulcast ready"-Claim deckt Recv-side-Demux, bis dieser Slice landet. Asymmetrische Fähigkeit. | **Mittel** | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md) (Guardrail „transport-only"); Memory-Anchor WebRTC-Roadmap | Recv-side-Demux als Folgearbeit, wenn SFU-Empfangsseite gefordert. |
| RTX + Keyframe-Feedback über den BUNDLE (H4) | RTX + PLI/NACK für `BundledVideoTrack` über den Bundle ist offenes Backlog-Item (auf dem separaten Video-Pfad ist Loss-Recovery via ADR-045 gebaut). | **Niedrig–Mittel** | AUDIT_HARDENING_BACKLOG HARD-H4; [ADR-045](../../adr/ADR-045-video-loss-recovery-nack-pli-rtx.md) | Nachziehen, wenn BUNDLE-Video mit Loss-Recovery gefordert. |
| SIP-BUNDLE-Adapter-Lücken (H7) | `BundledCallMediaSession`-Adapter (DTMF/RTCP-mux/Metrics/`IVideoMediaStream`) nur falls der SIP-BUNDLE-Pfad je verfolgt wird — nicht der WebRTC-Standardpfad. | **Niedrig** | AUDIT_HARDENING_BACKLOG HARD-H7 | Nur bei SIP-BUNDLE-Verfolgung relevant. |
| CORE-011 Soak/Interop/Chaos-Gates (P0) | Die formalen Stabilitäts-Gates (Soak/Interop/Chaos als CI-Gate) sind das letzte offene GA-Bedingungs-Item (A+B sind ✅). Die Interop-Suite existiert (Asterisk), aber ein durchgängiges Chaos-/Soak-CI-Gate ist noch nicht als geschlossen belegt. | **Mittel** | AUDIT_HARDENING_BACKLOG Paket I (CORE-011, P0); [ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md) | CORE-011 als letztes GA-Gate priorisieren. |

---

## Zusammenfassung für den Käufer

- **Reifer, interop-belegter Kern:** SIP-Signaling + Audio-Media (SDES/SRTP, DTLS-SRTP, RTCP, Codecs) sind gegen einen echten Asterisk end-to-end verifiziert (inkl. bidirektionaler Zwei-Bein-Media). Die kritischen Hardening- und Thread-Safety-Pakete (A, B, C, D, E, F) sind geschlossen.
- **Transport-vollständiger, aber interop-ungetesteter WebRTC-/TURN-/BUNDLE-Track:** funktional gebaut und unit-/loopback-getestet, aber **ohne Browser- und ohne Echt-TURN-Server-Nachweis**. Kein „production-ready"-Claim für WebRTC zulässig, bis diese Läufe existieren.
- **Bewusste Deferrals:** native Video-Codecs, SCTP-DataChannels, recv-side Simulcast, TCP/TLS-Relay-Datenpfad, TWCC-über-BUNDLE — alle als Roadmap geführt, keine stillen Lücken.
- **Ehrlichkeits-Disziplin:** ADR-Errata (006/011/038) und die „honest open edges"-Abschnitte belegen, dass die Doku aktiv auf den realen Code korrigiert wird — die tragende Eigenschaft dieses Registers.
