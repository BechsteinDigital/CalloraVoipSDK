# Produkt-Roadmap und realer Stand

*Teil des kommerziellen Due-Diligence-Pakets.*
Stand: 2026-07-27 · Grundlage: CEO-Vision (intern, nicht Teil des Pakets) (Phasen-Roadmap), ADR-Verzeichnis, sowie die technischen Belegdokumente [Fähigkeiten-/Reifegrad-Matrix](../technical/capabilities-matrix.md) und [Risiken- und Offene-Punkte-Register](../technical/risks-and-open-items.md).

## Zweck und Lesart

Diese Seite zeigt dem Käufer **drei Dinge zusammen**: die geplante Phasen-Roadmap (Produktrichtung), den **realen Ist-Stand** je Phase, und die konkret **verbleibende technische Arbeit**. Sie ist bewusst konservativ formuliert und folgt derselben Disziplin wie das übrige Paket.

**Grundregel „Doku ≤ Nachweis":** Jede Stand-Aussage ist mit einem ADR und/oder Code belegt. Es werden **keine Termine, keine Story-Points und keine Fertigstellungsquoten erfunden.** Wo eine Fähigkeit *im Code umgesetzt* ist, aber **nicht gegen einen realen Referenz-Stack** (Asterisk / FreeSWITCH / 3CX / Browser / echter TURN-Server) verifiziert wurde, wird „code-complete" strikt von „interop-verifiziert" getrennt — das ist der wichtigste Vorbehalt dieses Dokuments.

**Reifegrad-Vokabular** (identisch zur Fähigkeiten-Matrix):

| Begriff | Bedeutung |
|---------|-----------|
| **Gebaut & getestet** | Produktionstyp im `src/`-Baum, durch ADR und/oder Repo-Tests belegt. Kein bekannter Blocker im Kernpfad. |
| **Interop-verifiziert** | Zusätzlich end-to-end gegen echte Referenz-Stacks bewiesen (aktuell: der SIP-/Audio-Kern gegen **zwei** Stacks — Asterisk im PR-CI-Gate und FreeSWITCH lokal-first). |
| **Prototyp / ungetestet** | Baustein existiert im Code, aber nicht in den Produktionspfad verdrahtet oder nicht durch echten E2E-/Wire-Test abgesichert. |
| **Nicht gebaut** | Kein Produktionstyp — nur Roadmap/Vision/ADR-Vorschlag. |

---

## 1. Phasen-Roadmap (Produktrichtung) mit ehrlicher Ist-Einordnung

Die Produktrichtung stammt aus der CEO-Vision (intern, nicht Teil des Pakets): ein souveräner Telefonie-Kern (Phase 1), darauf Dialer-/Contact-Center-Enablement (Phase 2), darauf Privacy-first Voice Intelligence (Phase 3). Der Stand je Phase ist unten gegen den belegbaren Code eingeordnet.

### Phase 1 — Souveräne Calling-Basis

**Vision:** register / dial / accept / hangup / transfer / conference / media routing als Ersatz für teure oder unflexible Fremd-SDKs.

**Ist-Stand: weitgehend gebaut & getestet; der Audio-/SIP-Kern ist zusätzlich gegen zwei echte SIP-Stacks (Asterisk im CI-Gate + FreeSWITCH lokal-first) interop-verifiziert.**

| Baustein | Stand | Beleg |
|----------|-------|-------|
| SIP-Signaling (REGISTER + Digest-Auth, INVITE/UAC+UAS, CANCEL/BYE/ACK/Re-INVITE, UDP/TCP/TLS) | Gebaut & getestet | Matrix §1; [ADR-017](../../adr/ADR-017-register-expires-lifecycle.md), [ADR-018](../../adr/ADR-018-challenge-driven-digest-auth.md), [ADR-022](../../adr/ADR-022-invite-transaction-robustness.md) |
| Hold/Unhold, blinder + attended Transfer (REFER), Redirect, Dialog-Route-Set | Gebaut & getestet | Matrix §1; [ADR-020](../../adr/ADR-020-dialog-route-set-record-route.md), [ADR-002](../../adr/ADR-002-uas-redirect-redirect-async.md) |
| RTP/RTCP, SRTP (SDES), DTLS-SRTP, SRTCP, Jitter-Buffer, Media-Hotpath-Hardening | Gebaut & getestet | Matrix §3–4; [ADR-026](../../adr/ADR-026-srtp-media-path-fail-closed-hardening.md), [ADR-028](../../adr/ADR-028-dtls-srtp-foundation.md)…[ADR-031](../../adr/ADR-031-srtcp-crypto-core-and-rtcp-path-wiring.md) |
| Audio-Codecs (G.711 µ-law/A-law, Opus via Concentus), Devices, Media-Tap, Bridge-Transcoding, Konferenz/Mixing | Gebaut & getestet | Matrix §8; [ADR-049](../../adr/ADR-049-opus-codec-integration-concentus.md), [ADR-050](../../adr/ADR-050-bridge-audio-transcoding-opus-mulaw.md), [ADR-059](../../adr/ADR-059-public-media-tap-contract.md) |
| `VoipClient`/`IVoipClient` als zentrale Runtime-Facade, DDD-Layering, API-Versionierung | Gebaut & getestet | Matrix §12; [ADR-006](../../adr/ADR-006-api-versioning-strategy.md), [ADR-014](../../adr/ADR-014-ddd-layering-gated-baselines.md) |
| **SIP-/Audio-Kern gegen zwei echte SIP-Stacks (Asterisk im CI-Gate + FreeSWITCH lokal-first, 2-Bein-Media bidirektional)** | **Interop-verifiziert** | Risiko-Register §2; [ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md) (Asterisk 29 grün; FreeSWITCH gleiche `IPbxFixture`-Szenario-Matrix) |

**Ehrliche Rest-Kanten der Phase 1** (Details im [Risiko-Register](../technical/risks-and-open-items.md)):

- **Interop gegen zwei Stacks belegt, mit Rest-Kanten.** Der SIP-/Audio-Kern ist gegen **Asterisk** (im PR-CI-Gate) **und FreeSWITCH** (lokal-first, gleiche `IPbxFixture`-Szenario-Matrix) grün — identischer Testcode auf zwei Herstellern = Konformitätssignal. Offen: die **Aufnahme von FreeSWITCH ins PR-CI-Gate** (heute lokal-first) und **weitere Stacks** (3CX / Fritzbox), noch nicht als grüne Suite belegt (Risiko-Register §2, niedrig bzw. niedrig–mittel).
- **CORE-011 Soak/Interop/Chaos-CI-Gate offen.** Das ist das letzte offene GA-Bedingungs-Item; die Interop-Suite existiert (Asterisk), ein durchgängiges Soak-/Chaos-CI-Gate ist noch nicht als geschlossen belegt (Risiko-Register §9, mittel; `AUDIT_HARDENING_BACKLOG` Paket I).
- **Session-Timer-Refresher-Enforcement-Loop** nur teilweise: Aushandlung/Offer-Emission belegt, der aktive session-beendende Refresh-Timer nicht end-to-end (Matrix §1; [ADR-023](../../adr/ADR-023-session-timer-negotiation.md)).
- **ICE-Consent-Freshness/Restart auf dem SIP-Pfad = unverdrahtete Primitive.** RFC-7675-Verhalten wird für SIP-Calls **nicht** behauptet (nur BUNDLE/WebRTC-Pfad live; [ADR-041](../../adr/ADR-041-consent-freshness-and-ice-restart-primitives.md)).
- **Opus ist managed** (Concentus, kein Hardware-Codec), Tuning nicht konfigurierbar, PLC/FEC bei Loss ungenutzt, laut [ADR-049](../../adr/ADR-049-opus-codec-integration-concentus.md) nicht produktionsbewiesen.

> **Einordnung für den Käufer:** Phase 1 ist die reife Schicht des Produkts. Der Kern ist funktional vollständig und der Audio-/SIP-Pfad ist der **einzige** Teil des SDK mit einem echten Referenz-Interop-Nachweis — und dort gleich gegen **zwei** unabhängige Stacks (Asterisk + FreeSWITCH). Die offenen Punkte sind FreeSWITCH-CI-Gating, weitere Interop-Breite (3CX/Fritzbox), das formale Stabilitäts-CI-Gate und einzelne unverdrahtete SIP-Pfad-Primitive — keine strukturellen Lücken.

### Phase 2 — Dialer + Contact-Center-Enablement

**Vision:** Progressive Dialer, Agent-Routing, Kampagnen-Caller, Recording/Playback/Device-Controls/Quality-APIs.

**Ist-Stand: die Enabling-Bausteine sind gebaut (Phase-1-Kern), die Contact-Center-Fachlogik selbst ist nicht als eigenes Produkt gebaut.**

Was aus Phase 1 direkt als Fundament trägt: Recording/Playback und Device-Controls (Matrix §8), Quality-APIs (`CallQualitySnapshot`, RTCP-Monitor), der Media-Tap als Integrationspunkt ([ADR-059](../../adr/ADR-059-public-media-tap-contract.md)), Konferenz/Mixing, sowie der `ModuleRegistry`-Extensibilitäts-Seam für aufgesetzte Module ([ADR-007](../../adr/ADR-007-host-centric-platform-split.md)).

**Ehrliche Rest-Kanten:**

- Dialer-, Agent-Routing- und Kampagnen-Logik sind **nicht als `src/`-Produktkomponenten gebaut** — sie sind als Aufsatz auf dem Kern und dem `ModuleRegistry` vorgesehen, nicht als vorhandene Fertigware.
- **Inbound-Custom-INVITE-Header** (z. B. Contact-Center-Prefetch per Custom-Key) sind über `ICall`/`IncomingCall` **nicht** exponiert — offener PO-Kandidat (Matrix §12, „Nicht gebaut").
- **Lokaler MOS-Schätzwert fehlt**, wenn der Peer kein RTCP-XR sendet (Risiko-Register §2, niedrig) — relevant für Contact-Center-Quality-Dashboards.

> **Einordnung:** Phase 2 ist ein **Enablement-Versprechen**: die Kernprimitive existieren, die produktisierte Dialer-/CC-Schicht ist Roadmap. Kein Overclaim einer fertigen Contact-Center-Suite.

### Phase 3 — Privacy-first Voice Intelligence

**Vision:** AMD, Spam-/Scam-Screening, Sentiment-/Eskalationssignale, LLM-ready aber kontrolliert, lokale/europäische/datensparsame Pfade — geliefert über die vier Differenzierungsmodule `CalloraVoipSdk.Privacy` / `.Risk` / `.Intelligence` / `.Policy`.

**Ist-Stand: Vision, nicht gebaut. Kein `src/`-Projekt für eines der vier Module.**

| Modul | Stand | Beleg |
|-------|-------|-------|
| `CalloraVoipSdk.Privacy` (Redaction, Consent, Policy-Gates, Audit) | Nicht gebaut | Matrix §13; CEO_VISION (intern, nicht Teil des Pakets) Phase 3 |
| `CalloraVoipSdk.Risk` (Spam-/Scam-Signale, Call-Risk-Screening) | Nicht gebaut | Matrix §13 |
| `CalloraVoipSdk.Intelligence` (AMD, Sentiment, Transcript, lokale Modelle) | Nicht gebaut | Matrix §13; Integrationspunkt = Media-Tap [ADR-059](../../adr/ADR-059-public-media-tap-contract.md) |
| `CalloraVoipSdk.Policy` (Tenant-Regeln, Decision-Profiles, Compliance) | Nicht gebaut | Matrix §13 |

Der einzige **gebaute** Anknüpfungspunkt für Phase 3 ist der öffentliche Per-Call-Media-Tap ([ADR-059](../../adr/ADR-059-public-media-tap-contract.md), synchron/encoded/Fan-Out): über ihn kann Intelligence-Logik andocken. Namensähnliche vorhandene Bausteine (z. B. `SipWireTraceRedactionTests` = Trace-Redaction im Logging-Pfad) sind **kein** Privacy-Modul.

> **Einordnung:** Phase 3 ist bewusst als kommerzielle Differenzierung/Zukunft geführt. Für den Käufer ist der Media-Tap-Contract der Hebel — die Module selbst sind zu bauen.

---

## 2. WebRTC-/TURN-Stand (code-complete vs. Prototyp vs. offen)

Der WebRTC-/BUNDLE-/TURN-Track ist der zweite große Arbeitsstrang. Er ist überwiegend **funktional gebaut und SDK↔SDK bzw. gegen Fake-Server über Loopback getestet**. **Zwei erste reale Interop-Nachweise sind seit 2026-07-27 erbracht** (siehe [`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md), Coverage-Notizen Paket 1+2): der **TURN-Relay-Datenpfad gegen einen echten in-Process-`TurnServer`** (Loopback) und der **WebRTC-Kern gegen einen echten Chrome** (Connect + bidir. Audio, lokal-first) — inkl. behobenem mDNS-`.local`-Blocker. Das hebt die frühere „gar kein realer Nachweis"-Grenze auf, ist aber **kein production-ready-Beleg**: die GA-Reifung läuft (Video-Browser-Interop, Browser-Offerer-Richtung, CI-Gating, externer coturn offen). Die Guardrail aus [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) bleibt: **kein „production-ready"-Claim für WebRTC ohne vollständige Browser-Interop-Validierung.**

### Was code-complete ist (gebaut & getestet, SDK↔SDK)

- **BUNDLE-Transport** (ein 5-Tupel, eine DTLS-Assoziation, geteilte SRTP/SRTCP, RTP/RTCP/STUN/DTLS-Demux, Track-Routing) — der zentrale Transport-Umbau aus [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) §2 / [ADR-010](../../adr/ADR-010-bundle-transport-slice-plan.md)/[ADR-011](../../adr/ADR-011-rtp-multitrack-transport.md).
- **Öffentliche WebRTC-Fassade** `WebRtcClient`/`IPeerConnection`, fluente ICE-Server-Konfiguration (`WithStunServer`/`WithTurnServer`/`WithIceServers`, akkumulierend), **Send-Side-Simulcast** — [ADR-012](../../adr/ADR-012-webrtc-public-facade.md), [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md).
- **SDK↔SDK-Peer-Loopback** über BUNDLE + DTLS-SRTP + ICE (`WebRtcPeerLoopbackTests`) — Matrix §10.
- **TURN-Client-Control-Stack**: Allocate / CreatePermission / ChannelBind / Refresh, Allocation-Refresh-/Teardown-Keepalive-Loop, TURN-Relay als First-Class-ICE-Kandidat (UDP-Gathering + Send-Path) — [ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md), [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md).
- **TURN-/STUN-Server-Hosting-Fassade** (`AddCalloraTurnServer`/`AddCalloraStunServer`, `ITurnServerHost`/`IStunServerHost`) — hebt die vorher test-only Server auf eine Produktoberfläche ([ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md)); TURN-Methoden + STUN-Binding je E2E-bewiesen, aber **kein Real-World-Betriebsnachweis unter Last**.
- **TCP/TLS-TURN-Control-Pfad** (Allocate/Refresh über Stream-Transport) — gebaut & getestet ([ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md)).

### Was neu real-server-E2E- / browser-belegt ist (2026-07-27)

- **TURN-Relay-Datenpfad gegen echten in-Process-`TurnServer`** (Loopback): vier reale-Server-E2E-Tests — `TurnServerE2eTests` (Allocation/Relay), `TurnPublicRelayAddressTests` (Public-Relay-Address), `TurnServerIndicationAuthE2eTests` (Indication-Auth), `TurnRelayKeepAliveE2eTests` (Keepalive/Refresh past Lifetime + ChannelData-Zustellung, CF-003, „closes the only-fake-server-coverage gap"). **Schließt die frühere „nur-Fake-Server"-Lücke.** Offen bleibt der Nachweis gegen einen **externen** Produktions-TURN-Server (coturn) und ein TURN-durchquerender Browser-Call ([`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md)).
- **WebRTC-Kern gegen echten Chrome — Audio, SDK-Offerer** (lokal-first): `CalloraVoipSdk.BrowserInteropTests` (Playwright/headless Chromium) zeigt SDK-Offerer ↔ Browser-Answerer, volle Kette Signaling→ICE→DTLS→SRTP, **bidir. Opus-Audio** (3× stabil). Dabei behobener GA-Blocker: SDK löst Chromes `.local`-mDNS-Candidates jetzt auf (RFC 8828). **Grenzen:** nur Audio, nur SDK-Offerer, Loopback host-only, Kategorie `BrowserInterop` **aus allen CI-Jobs ausgeschlossen** → **kein production-ready-Claim** ([`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md), Coverage-Notizen Paket 1+2).

### Was Prototyp-Stufe ist (im Code, aber ohne echten E2E-Nachweis)

- **Post-Nomination-Whole-Socket-Relay-Transition** (Direct → ChannelBind → ChannelData, [ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md)): Transport-Primitive (`EnterRelayMode`/`SetRelayChannel`) und Orchestrierung sind **einzeln** getestet, aber es gibt **keinen vollen Session-ChannelData-Roundtrip in einem schnellen Test** — die Relay-Nominierung über einen echten Socket ist timeout-gebunden (~10 s Direct-Exhaustion). Ein Orchestrierungs-E2E nutzt ein IPv6-totes Direct-Paar, um `SetRelayChannel` schnell zu erreichen. Der **Relay-Control-/Datenpfad selbst** (Allocation/Permission/ChannelBind/Keepalive/ChannelData) ist inzwischen gegen einen echten in-Process-`TurnServer` E2E-belegt (s. „Was neu real-server-E2E-belegt ist"); **kein externer-coturn-E2E, kein TURN-über-Browser-Call** → **kein Production-Ready-Claim** (Matrix §6).
- **Inbound/Outbound-TWCC im BUNDLE-Pfad**: Feedback-Sender + Estimator existieren, aber `transportWideCcExtensionId` ist im BUNDLE-Pfad auf `null` (nicht verdrahtet); die Controller-Platzierung ist ein offener Design-Fork (Matrix §9; SESSION_HANDOFF #7 Slice 5–6).

### Was offen / nicht gebaut ist

- **Browser-Interop: Video (VP8) + Browser-Offerer-Richtung** — nicht gebaut. Audio + SDK-Offerer sind belegt (s. o.); Video-über-Browser und Browser-initiiert (SDK antwortet) bleiben offen ([ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) Guardrail).
- **CI-Aufnahme des Browser-Interop-Tests** — der Test ist lokal-first (`BrowserInterop`, aus allen CI-Jobs ausgeschlossen); die PR-CI-Gating (Playwright-Browser-Install-Step) ist ein Folge-Schritt.
- **Voll-E2E-Relay gegen externen coturn + TURN-über-Browser-Call (4d-6)** — nicht gebaut. Der Relay-Datenpfad ist gegen den **eigenen** in-Process-`TurnServer` belegt (s. o.); ein Nachweis gegen einen externen `coturn` und ein TURN-durchquerender Browser-Call fehlen.
- **Controlled-Agent-Relay** (Answerer besitzt den Relay bei direktem Offerer) — nicht gebaut, braucht Design ([ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md) „Controlled-agent relay gap").
- **TCP/TLS-Relay-**Daten**-Pfad** — nicht gebaut (nur der Control-Pfad ist bewiesen; [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md)/[ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md) „out of scope").
- **Native VP8/H264-Codecs, recv-side Simulcast-Demux, SCTP-DataChannels** — nicht gebaut (siehe §3).

---

## 3. Verbleibende technische Roadmap-Posten

Alle Posten sind mit ADR/Register belegt; die relative Einordnung (§4) nennt Reihenfolge und Grobaufwand **ohne** erfundene Termine.

| # | Posten | Warum offen / Umfang | Beleg |
|---|--------|----------------------|-------|
| 1 | **Browser-Interop-Reifung (Video + Browser-Offerer + CI-Gating)** | **Kern erledigt:** Audio-Browser-Interop (SDK-Offerer ↔ echter Chrome, Connect + bidir. Audio) ist bewiesen und der mDNS-`.local`-GA-Blocker behoben. **Rest:** Video/VP8-Browser-Interop, die Browser-Offerer-Richtung und die Aufnahme des lokal-first-Tests ins PR-CI-Gate. **Guardrail: bis diese Rest-Slices stehen, kein WebRTC-„production-ready".** | [`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md) (Coverage-Notizen Paket 1+2); [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md); Risiko-Register §2 (Mittel); `AUDIT_HARDENING_BACKLOG` HARD-H8/B6 |
| 2 | **Echter-externer-TURN-Server-E2E (4d-6)** | **Kern erledigt:** der Relay-Datenpfad (Allocation/Permission/ChannelBind/Keepalive/ChannelData) ist gegen einen echten in-Process-`TurnServer` E2E-belegt (vier Tests). **Rest:** Nachweis gegen einen **externen** `coturn` und ein TURN-durchquerender Browser-Call (Browser-E2E lief host-only ohne STUN/TURN). Voraussetzung für jeden NAT-durchquerenden Produktiveinsatz. | [`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md); [ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md)/[ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md)/[ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md); Risiko-Register §2 (Mittel) |
| 3 | **Controlled-Agent-Relay** | Nur der controlling Agent treibt Nominierung + installiert Relay-Permissions. Offerer-relay↔Answerer-direct funktioniert; Answerer-besitzt-Relay bei direktem Offerer **nicht**. Braucht Design (Nomination-Trigger + Permission-Install auf der Answerer-Seite). | [ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md) „Controlled-agent relay gap"; Risiko-Register §3 (Mittel) |
| 4 | **TCP/TLS-Relay-Datenpfad** | TURN-Stack ist UDP-Relay; TCP/TLS-*Control* ist bewiesen, aber ein persistenter **Stream-Relay-Datenpfad** (ChannelData-Framing + eigener Receive-Loop) ist ein großes, ungebautes Feature. WebRTC-`TurnAllocationProbe` ist UDP-Socket-gebunden → TCP/TLS-Relay-Gathering fehlt ganz. Nur relevant, wenn UDP-TURN im Zielnetz blockiert ist. | [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md)/[ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md) „out of scope"; Risiko-Register §3 (Mittel) |
| 5 | **Native VP8/H264-Encode/Decode** | Video ist **transport-only**: nur (De-)Packetiser (`Vp8Depacketiser`, `H264Depacketiser`, `AnnexBParser`), **kein** nativer Encoder/Decoder. Bewusste Produktentscheidung — App/Peer liefert Encoding. [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) §5 skizziert die Codec-Rollen (`IVideoEncoder`/`IVideoDecoder` + FFmpeg-Paket, VP8 → H.264) als Zielbild. | Matrix §7; Risiko-Register §9 (Mittel) |
| 6 | **SCTP-DataChannels** | WebRTC-DataChannels (`m=application` → DTLS → SCTP → DCEP → öffentliche API) sind ein späterer, ungebauter Slice; blockieren den Medien-MVP nicht. | [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) §7; Risiko-Register §9 (Mittel) |
| 7 | **Recv-side Simulcast-Demux** | Simulcast ist **send-side-only** (App besitzt Encoder, SDK packetisiert per rid). Empfangsseitiges rid/Layer-Demux fehlt — asymmetrische Fähigkeit. | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md) (Guardrail „transport-only"); Matrix §7; Risiko-Register §9 (Mittel) |
| 8 | **Differenzierungsmodule** (Privacy/Risk/Intelligence/Policy) | Phase-3-Kern; kein `src/`-Projekt. Media-Tap ([ADR-059](../../adr/ADR-059-public-media-tap-contract.md)) ist der vorgesehene Integrationspunkt. | Matrix §13; CEO_VISION (intern, nicht Teil des Pakets) Phase 3 |
| 9 | **RTX + Keyframe-Feedback über den BUNDLE** (H4) und **TWCC-über-BUNDLE-Verdrahtung** | RTX/PLI/NACK für `BundledVideoTrack` und die TWCC-Verdrahtung über den geteilten Transport sind offene Backlog-Items (auf dem separaten Video-Pfad ist Loss-Recovery via [ADR-045](../../adr/ADR-045-video-loss-recovery-nack-pli-rtx.md) gebaut). Design-forked (SESSION_HANDOFF #7 Slice 3–6). | Risiko-Register §5/§9; SESSION_HANDOFF #7 |
| 10 | **CORE-011 Soak/Interop/Chaos-CI-Gate** | Letztes offenes GA-Bedingungs-Item; formales Stabilitäts-Gate als CI noch nicht als geschlossen belegt. | Risiko-Register §9 (Mittel); `AUDIT_HARDENING_BACKLOG` Paket I |
| 11 | **FreeSWITCH-CI-Gating + Interop-Breite** | FreeSWITCH ist bereits lokal-first grün (gleiche `IPbxFixture`-Szenario-Matrix wie Asterisk), aber noch nicht ins PR-CI-Interop-Gate aufgenommen; 3CX/Fritzbox als weitere grüne Interop-Suite ausstehend. | Risiko-Register §2 (Niedrig bzw. Niedrig–Mittel); [ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md) |

---

## 4. Grobe Einordnung Aufwand und Reihenfolge

Diese Reihenfolge ist eine **Empfehlung aus technischer Abhängigkeit und Wirkbereich**, keine terminierte Planung. Es werden bewusst keine Story-Points oder Wochen angegeben — dafür fehlt der belastbare Nachweis, und das Paket vergibt keine erfundenen Zahlen.

**Reihenfolge-Logik (Abhängigkeiten):**

1. **Zuerst validieren, was schon gebaut ist**, bevor Neues gebaut wird — der höchste Wert liegt darin, den vorhandenen WebRTC-/TURN-Code von „code-complete" auf „interop-verifiziert" zu heben:
   - **Posten 1 (Browser-Interop-Reifung)** und **Posten 2 (externer TURN-E2E)** sind die eigentlichen Freigabe-Blocker für jeden WebRTC-/NAT-Produktiveinsatz. Ihr **Kern ist inzwischen belegt** (Audio-Browser-Interop + mDNS-Fix bzw. Relay-Datenpfad gegen echten in-Process-`TurnServer`); es verbleibt gezielte **Test-/Harness-Arbeit** (Video, Browser-Offerer, CI-Gating bzw. externer coturn), kein Neubau — der Datenpfad existiert bereits.
   - **Posten 10 (CORE-011 CI-Gate)** und **Posten 11 (FreeSWITCH-CI-Gating + Interop-Breite)** heben denselben Reifehebel für den bereits gegen zwei Stacks interop-verifizierten SIP-/Audio-Kern.

2. **Danach die kleineren, abgegrenzten Verdrahtungs-Posten**, die auf vorhandenen Bausteinen aufsetzen: **Posten 9** (RTX/TWCC über BUNDLE, design-forked) und **Posten 7** (recv-side Simulcast-Demux) — jeweils begrenzter Wirkbereich, Bausteine teils vorhanden.

3. **Größere, design-gated Features** mit eigenem Umfang und je eigener Design-Runde — sinnvollerweise nur bei konkreter Nachfrage:
   - **Posten 3 (Controlled-Agent-Relay)** — braucht Design, betrifft den sicherheitskritischen ICE-Kern.
   - **Posten 4 (TCP/TLS-Relay-Datenpfad)** — großes ungebautes Feature; nur relevant, wenn UDP-TURN im Zielnetz blockiert ist.
   - **Posten 5 (native VP8/H264-Codecs)** — bewusste Produktentscheidung „transport-only"; Zielbild in [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) §5 skizziert.
   - **Posten 6 (SCTP-DataChannels)** — eigenständiges Subsystem, nach dem Medien-MVP.

4. **Produkt-Differenzierung (Phase 3)** — **Posten 8** (Privacy/Risk/Intelligence/Policy) ist Netto-Neubau auf dem gebauten Media-Tap; Umfang und Reihenfolge richten sich nach der Käufer-Produktstrategie, nicht nach dem Kern.

**Aufwands-Grobklassen** (qualitativ, aus ADR-/Register-Formulierung abgeleitet):

| Klasse | Posten | Charakter |
|--------|--------|-----------|
| **Validierung / Harness** (kein Neubau, hoher Freigabewert) | 1, 2, 10, 11 | Testinfrastruktur gegen reale Gegenstellen; Datenpfad existiert |
| **Abgegrenzte Verdrahtung** (Bausteine da, design-forked) | 7, 9 | Begrenzter Wirkbereich, je eigener kleiner Design-Fork |
| **Große, design-gated Features** (Neubau, je eigene Design-Runde) | 3, 4, 5, 6 | Sicherheitskritischer Kern bzw. eigenständiges Subsystem |
| **Produkt-Differenzierung** (Netto-Neubau auf gebautem Seam) | 8 | Phase-3-Module; strategiegetrieben |

---

## Zusammenfassung für den Käufer

- **Phase 1 ist reif und teil-interop-verifiziert:** SIP-Signaling + Audio-Media sind gebaut, getestet und der SIP-/Audio-Kern ist gegen **zwei echte SIP-Stacks** end-to-end bewiesen — **Asterisk** (im PR-CI-Gate) **und FreeSWITCH** (lokal-first, gleiche Szenario-Matrix); identischer Testcode auf zwei Herstellern = Konformitätssignal. Offene Punkte sind FreeSWITCH-CI-Gating, weitere Interop-Breite (3CX/Fritzbox), das CORE-011-CI-Gate und einzelne unverdrahtete SIP-Pfad-Primitive.
- **Phase 2 ist enabled, nicht produktisiert:** Recording/Playback/Device-Controls/Quality-APIs/Media-Tap/`ModuleRegistry` tragen als Fundament; die Dialer-/Contact-Center-Fachlogik ist Aufsatz-Roadmap.
- **Phase 3 ist Vision:** die vier Differenzierungsmodule haben keinen `src/`-Code; der Media-Tap ist der gebaute Integrationspunkt.
- **WebRTC/TURN ist transport-vollständig mit erstem Interop-Nachweis, aber noch nicht GA:** BUNDLE, Fassade, Send-Side-Simulcast, TURN-Control-Stack und die Server-Hosting-Fassade sind SDK↔SDK gebaut & getestet; **neu belegt** sind der TURN-Relay-Datenpfad gegen einen echten in-Process-`TurnServer` und der WebRTC-Kern gegen einen echten Chrome (Connect + bidir. Audio, lokal-first, inkl. behobenem mDNS-Blocker). **Weiterhin kein „production-ready"-Claim** — offen bleiben Video-Browser-Interop, die Browser-Offerer-Richtung, die CI-Aufnahme und ein externer-coturn-Nachweis (Guardrail [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md)).
- **Die höchstwertige nächste Arbeit ist Validierung, nicht Neubau:** die Browser-Interop-Reifung (Posten 1: Video, Browser-Offerer, CI-Gating) und der externe TURN-E2E (Posten 2: coturn, TURN-über-Browser) heben den bereits im Kern belegten Code auf Produktionsreife.

*Bezugsdokumente: [Fähigkeiten-/Reifegrad-Matrix](../technical/capabilities-matrix.md), [Risiken- und Offene-Punkte-Register](../technical/risks-and-open-items.md), [ADR-Index](../../adr/README.md), CEO_VISION (intern, nicht Teil des Pakets). Verifikationsmethode: ADR-Consequences/Guardrails je Posten + Abgleich gegen die beiden technischen Belegdokumente.*
