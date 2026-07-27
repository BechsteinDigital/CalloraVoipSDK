# Käufer-Übergabepaket — Executive Overview

*Einstiegsseite des Übergabepakets. Stand: 2026-07-27.*

Diese Seite ist der Einstiegspunkt für Käufer und deren technische wie
kaufmännische Due Diligence. Sie fasst zusammen, **was CalloraVoipSdk ist**,
**was es ausdrücklich nicht ist**, **wie reif es tatsächlich ist** und **wie das
Übergabepaket zu lesen ist**. Sie ist bewusst konservativ formuliert: Wo
Marketing- oder Vision-Material optimistischer klingt als der belegbare Code,
gewinnt der Code — Details und Belege stehen in der
[Fähigkeiten- und Reifegrad-Matrix](technical/capabilities-matrix.md) und der
[Risiken- und Offene-Punkte-Seite](technical/risks-and-open-items.md).

---

## Eckdaten

| | |
|---|---|
| **Produkt** | CalloraVoipSdk — kommerzielles VoIP-SDK (B2B) |
| **Version** | `4.6.0-preview.2` (Preview-Linie `4.x`; kein stabiles `1.0`-Release) |
| **Plattform** | .NET — TFMs `net8.0`, `net9.0`, `net10.0` |
| **Architektur** | Domain-Driven Design, klare Schichtung (Domain / Application / Infrastructure / Sdk) |
| **Zentrale Fassade** | `VoipClient` / `IVoipClient` |
| **Lizenzrichtung** | gestaffelt: Developer/Starter · Commercial · OEM/Enterprise; Zusatzangebote: Maintenance, Support/SLA, Integrationsberatung, Premium-Module, White-Label |

---

## Was CalloraVoipSdk ist

CalloraVoipSdk ist ein **kommerzielles .NET-VoIP-SDK** mit einem **souveränen,
eigenen SIP-/RTP-/SRTP-Stack** — ohne Laufzeitabhängigkeit auf externe
SIP-Stacks. Es ist der kontrollierbare **Unterbau** für Teams, die eigene
Voice-Produkte bauen:

- **PBX- und UC-Anbieter**
- **Contact-Center-Softwarehersteller**
- **Dialer- und Kampagnen-Tools**
- **Voice-AI-/Voicebot-Plattformen** und andere Integratoren mit eingebetteter Telefonie

Der Kern liefert Signaling, Medienpfad und Session-Lifecycle mit voller
technischer Kontrolle über Telefonie, Media-Security und Privacy — angeboten über
eine stabile, engine-agnostische Public API (`VoipClient`). Die Codebasis folgt
durchgängig Domain-Driven Design mit gated, architektur-getesteten
Schichtgrenzen.

## Was CalloraVoipSdk ausdrücklich nicht ist

- **kein Endkunden-Softphone** — keine fertige Anruf-UI
- **kein CRM** — keine Kontakt-/Deal-/Ticket-Verwaltung
- **kein gehosteter PBX-Dienst / SaaS** — es ist ein einzubettendes SDK, kein Betriebsservice
- **keine Callcenter-Komplettsoftware** und **kein beliebiger AI-Wrapper**

Der Käufer erwirbt eine **Runtime und APIs**, nicht ein fertiges Endprodukt.

---

## Reifegrad (ehrliche Kurzeinschätzung)

Der **SIP-/RTP-Kern und der Media-Security-Pfad sind belastbar**: SIP-Signaling
(REGISTER/INVITE-Dialog/BYE/CANCEL/Re-INVITE, Hold/Transfer, UDP/TCP/TLS),
RTP/RTCP-Transport mit Jitter-Buffer, SRTP/SDES und DTLS-SRTP sowie der
**Video-Transportpfad** (Packetisierung, Reorder-Playout, Loss-Recovery-Feedback)
sind im `src/`-Baum gebaut und durch Unit-/Integrationstests belegt.
**WebRTC-Fassade und TURN-Relay-Datenpfad sind teils Prototyp** (Bausteine
gebaut, aber nicht vollständig in den Produktionspfad verdrahtet bzw. nicht
End-to-End gegen einen realen Stack abgesichert). **Browser-Interop ist
unbewiesen** — WebRTC ist nur SDK↔SDK (Loopback) validiert, nie gegen einen
echten Browser; native Video-Codecs sind **transport-only** (kein Encode/Decode).
Die **Differenzierungsmodule** (Privacy / Risk / Intelligence / Policy) sind
**Vision, nicht gebaut** — es existiert dafür kein `src/`-Projekt.

Wichtig für die DD: **„getestet" heißt hier durch Repo-Tests belegt — nicht durch
Interop gegen reale Referenz-Stacks (Asterisk/FreeSWITCH/3CX/Browser) oder unter
Soak-Last.** Eine solche Interop-/Soak-Suite steht noch aus. Die vollständige,
fähigkeit-für-fähigkeit belegte Einschätzung mit Reifegrad-Stufen steht in der
→ [Fähigkeiten- und Reifegrad-Matrix](technical/capabilities-matrix.md);
die zusammengefassten Kernlücken und offenen Punkte in der
→ [Risiken- und Offene-Punkte-Seite](technical/risks-and-open-items.md).

---

## Inhalt des Übergabepakets

Das Paket besteht aus einem **technischen** und einem **kaufmännischen** Teil.

### Technischer Teil (`technical/`)

| Seite | Inhalt |
|-------|--------|
| [`capabilities-matrix.md`](technical/capabilities-matrix.md) | Ehrliche Fähigkeiten- und Reifegrad-Matrix, je Fähigkeit gegen Code + ADR belegt (Grundregel „Doku ≤ Nachweis"). |
| [`architecture.md`](technical/architecture.md) | Schichtenmodell, Modulgrenzen und wesentliche Bausteine des SIP-/RTP-/SRTP-Stacks. |
| [`protocol-conformance.md`](technical/protocol-conformance.md) | RFC-Abdeckung und Konformitätsstand des Signaling-/Media-Pfads. |
| [`quality-and-testing.md`](technical/quality-and-testing.md) | Teststrategie, Testabdeckung und Grenzen des vorhandenen Nachweises. |
| [`risks-and-open-items.md`](technical/risks-and-open-items.md) | Konsolidierte Kernlücken, technische Risiken und offene Arbeitspakete. |

### Kaufmännischer Teil (`commercial/`)

| Seite | Inhalt |
|-------|--------|
| [`product-positioning.md`](commercial/product-positioning.md) | Marktpositionierung, Zielkunden, Abgrenzung und Lizenzmodell. |

### Ergänzende Quellen

- **Architecture Decision Records** → [`../adr/README.md`](../adr/README.md) — die
  belastbaren, gegen den Code verifizierten Entwurfsentscheidungen. Jede
  Fähigkeit in der Matrix ist über eine ADR und/oder Testklasse belegt.

---

## Leseempfehlung

1. **Diese Seite** — Kontext, Abgrenzung, Reifegrad-Überblick.
2. **Kaufmännisch orientiert:** weiter mit
   [`commercial/product-positioning.md`](commercial/product-positioning.md).
3. **Technisch orientiert:** [`technical/capabilities-matrix.md`](technical/capabilities-matrix.md)
   (was ist real belegt) → [`technical/risks-and-open-items.md`](technical/risks-and-open-items.md)
   (wo sind die Lücken) → [`technical/architecture.md`](technical/architecture.md) und
   [`technical/protocol-conformance.md`](technical/protocol-conformance.md) für die Tiefe.
4. **Für Entwurfstiefe:** die verlinkten [ADRs](../adr/README.md).
