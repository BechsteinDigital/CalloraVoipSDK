# CalloraVoipSdk — Übergabepaket (Käufer-Due-Diligence)

Stand: 2026-07-27

Dies ist die Vordertür des Übergabepakets für einen potenziellen Käufer des CalloraVoipSdk.
Es bündelt einen **technischen** und einen **kommerziellen** Due-Diligence-Teil. Alle Seiten
folgen einer harten Regel: **keine Aussage behauptet mehr, als der Code/das Register hergibt.**
Reifegrade und Risiken sind bewusst ehrlich dargestellt — das ist für eine DD wertvoller als
Schönfärberei.

## Für wen welcher Teil

- **Technischer Käufer / übernehmendes Dev-Team** → beginne mit dem technischen Teil und dem
  ADR-Register.
- **Strategischer / kommerzieller Käufer** → beginne mit der Executive Overview und dem
  kommerziellen Teil.

## Einstieg

1. [Executive Overview](01-executive-overview.md) — was das SDK ist, Positionierung, ehrlicher Reifegrad in einer Seite.

## Technischer Due-Diligence-Teil

| Seite | Inhalt |
|-------|--------|
| [Architektur](technical/architecture.md) | DDD-Schichtung, reale Modulkarte, Layer-Regeln und deren mechanische Durchsetzung. |
| [Fähigkeiten-/Reifegrad-Matrix](technical/capabilities-matrix.md) | Ehrliche Matrix: gebaut & getestet / teilweise / Prototyp-ungetestet / nicht gebaut — je mit Beleg und Caveat. |
| [Protokoll-Konformität](technical/protocol-conformance.md) | Verdichtete RFC-Abdeckung (voll / teilweise / nicht), code-verifiziert. |
| [Qualität & Tests](technical/quality-and-testing.md) | L0–L4-Testmodell, CI-Gates, Engineering-Rules, Audit-/Findings-Disziplin — und was NICHT getestet ist. |
| [Risiken & offene Punkte](technical/risks-and-open-items.md) | Ehrliches Risiko-/Offene-Punkte-Register mit Schwere-Einstufung und empfohlener Maßnahme. |
| [Build, Run & Erweitern](technical/build-run-extend.md) | Bauen/Testen/Ausführen + Erweiterungspunkte (Module, Media-Tap, DI, WebRTC/Hosting). |
| [IP & Provenienz](technical/ip-provenance.md) | Eigener Stack, Laufzeit-Abhängigkeiten mit Lizenzen, SDK-Lizenz, ehrliche IP-Hinweise. |

## Kommerzieller Due-Diligence-Teil

| Seite | Inhalt |
|-------|--------|
| [Produkt-Positionierung](commercial/product-positioning.md) | Kernbotschaft, Zielmärkte, Abgrenzung, Differenzierung (Vision vs. Ist getrennt). |
| [Roadmap](commercial/roadmap.md) | Phasen-Roadmap + realer Stand + verbleibende Arbeit (ohne erfundene Termine). |
| [Lizenz-/Monetarisierungsmodell](commercial/licensing-model.md) | Lizenzstufen (Richtung), Modul-Store, technische Lizenzgrundlage (Apache-2.0, SemVer). |
| [F&E- & Compliance-Wertsignal](commercial/rnd-compliance-value.md) | Verdichtete F&E-Tiefe + DSGVO/EU-AI-Act-Haltung — **ohne** Finanz-/Förder-Rohdaten. |

## Tiefergehende Belege

- **Architektur-Entscheidungen:** das vollständige [ADR-Register](../adr/README.md) (61 ADRs,
  code-verifiziert) begründet das *Warum* hinter der Architektur. Es ist auch als Sektion
  „Architecture Decisions" in der veröffentlichten DocFX-Doku enthalten.
- **Technische Referenz:** [reference/](../reference/README.md) (SemVer-Policy, Plugin-Contract,
  WebSocket-Protokoll, Entscheidungs-Inventar).
- **Nutzer-/Consumer-Doku:** das DocFX-Portal unter `../portal/`.

## Bewusst nicht Teil dieses Pakets

Interne Geschäftsunterlagen (F&E-Förderakte mit Stunden-/Finanzdaten, interne
CEO-Entscheidungen, laufende Eskalationen) sowie die rohen historischen Status-/Log-Dokumente
liegen intern und sind **nicht** Bestandteil dieses Pakets. Das kommerzielle Wertsignal daraus
ist im [F&E- & Compliance-Teil](commercial/rnd-compliance-value.md) verdichtet; Rohunterlagen
nur auf Anfrage in einer späteren DD-Phase / unter NDA.
