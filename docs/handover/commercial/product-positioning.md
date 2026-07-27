# Produktpositionierung

*Teil des kommerziellen Due-Diligence-Pakets.*
Stand: 2026-07-27 · Zielleser: strategischer / kommerzieller Käufer

Diese Seite beschreibt **Positionierung, Zielmärkte und Differenzierung** des
CalloraVoipSdk aus kommerzieller Sicht. Sie trennt bewusst **Vision** von **Ist-Stand**:
Wo eine Aussage die strategische Ausrichtung beschreibt und nicht den heute gebauten
Code, ist das explizit als *Vision* markiert. Der belegbare technische Reifegrad steht
in der [Fähigkeiten- und Reifegrad-Matrix](../technical/capabilities-matrix.md) — diese
Positionierungsseite behauptet an keiner Stelle mehr, als dort nachgewiesen ist.

**Grundregel „Doku ≤ Nachweis":** Marktpositionierung darf Zielbild sein.
Produkt-Reifegrad-Aussagen sind es nicht — sie sind gegen die Capabilities-Matrix
konsistent gehalten. Module und Fähigkeiten, die noch nicht gebaut sind, werden als
Vision gekennzeichnet und **nicht** als vorhandenes Produkt dargestellt.

---

## 1. Kernbotschaft und Mission

> **„Build your own voice product on a sovereign telephony core."**

CalloraVoipSdk ist eine **europäische B2B-Voice-Runtime** für Teams, die eigene Calling-,
Dialer-, Contact-Center- oder Voice-AI-Produkte bauen wollen — mit voller technischer
Kontrolle über Telefonie, Medienpfad, Privacy und Entscheidungslogik.

Der strategische Kern ist ein **souveräner Telefonie-Core**: ein eigener, in .NET
implementierter SIP/RTP/SRTP/VoIP-Stack ohne Laufzeitabhängigkeit auf externe SIP-Stacks.
Wer auf diesem Core baut, besitzt den kompletten Signaling- und Medienpfad selbst —
statt ihn an eine fremde Bibliothek, eine Cloud-CPaaS-API oder eine Blackbox-PBX abzugeben.

Der kommerzielle Wert liegt in genau dieser **Souveränität**:

- **Kontrolle ohne Zwang zur Komplexität** — fertige Workflows für den Einstieg und
  öffentliche, typisierte Call-, Medien- und Erweiterungsverträge für Produktlogik,
  die tiefer gehen muss.
- **Privacy / EU** — self-hostbar; Gesprächsdaten und Medien müssen weder eine fremde
  Cloud noch eine außereuropäische Jurisdiktion durchlaufen. Das ist der Aufhänger der
  „your data stays yours"-Positionierung (siehe auch [ADR-008](../../adr/ADR-008-community-module-store.md),
  Media-Tap-Missbrauchsschutz).
- **Unabhängigkeit** — keine Laufzeitbindung an einen Fremd-Stack, keine Per-Minute-CPaaS-Abrechnung
  im Medienpfad, kein Vendor-Lock-in im Kern.

---

## 2. Zielkunden (B2B)

CalloraVoipSdk richtet sich an **Produkt- und Plattformhersteller**, die Telefonie tief in
ihr eigenes Produkt einbetten — nicht an Endanwender.

| Zielkunde | Wofür sie den Core nutzen |
|-----------|---------------------------|
| **PBX- / UC-Anbieter** | eigener Softswitch / Nebenstellen-Backend statt Fremd-SDK |
| **Contact-Center-Softwarehersteller** | Agent-Routing, Recording, Media-Cross-Connect als Produktkern |
| **Dialer- / Kampagnen-Tools** | Progressive / Outbound-Dialer mit voller Kontrolle über Rufaufbau und Medien |
| **CRM- / Sales-Automation mit Calling** | eingebettete Telefonie im CRM (Click-to-Dial, Screen-Pop, Writeback) |
| **Voicebot- / AI-Agent-Plattformen** | Medienpfad-Zugriff für STT/TTS/LLM-Anbindung mit kontrollierten, lokalen/europäischen Datenwegen |
| **Fraud- / Spam- / Scam-Detection** | Signal-Extraktion aus dem Call für Risk-Screening |
| **Branchenlösungen mit eingebetteter Telefonie** | Healthcare, Legal, Finance, Behörden — Voice als Feature einer Fachlösung |

Gemeinsamer Nenner: **Diese Kunden verkaufen ein eigenes Produkt** und brauchen einen
kontrollierbaren, einbettbaren Unterbau — kein fertiges Endprodukt, das mit ihrem eigenen
konkurriert.

---

## 3. Abgrenzung — was es *nicht* ist

Die Abgrenzung ist Teil der Positionierung: CalloraVoipSdk konkurriert bewusst **nicht** mit
den Produkten seiner eigenen Zielkunden.

- **Kein Endkunden-Softphone** — kein fertiges Telefon-UI für Endanwender.
- **Keine Contact-Center-Komplettsoftware** — kein monolithisches, out-of-the-box
  All-in-one-Callcenter-Paket.
- **Kein CRM.**
- **Kein gehosteter PBX-Dienst** — kein Managed-Cloud-Telefonanlagen-Angebot als Produkt.
- **Kein beliebiges AI-Wrapper-Projekt.**

Positiv formuliert (nach der „Shopware für Voice"-These, siehe
VOICE_PLATFORM_IDEE (intern)): **stabiler Core mit klaren
Extension Points**, auf dem Kunden und Drittanbieter vertikale Funktionen bauen — nicht die
nächste fertige All-in-one-Software.

---

## 4. Produktstruktur

Die Produktvision gliedert sich in **zwei Ebenen**: den souveränen Core und darüber
gelagerte Differenzierungsmodule. Der Reifegrad der beiden Ebenen ist **grundverschieden**
und wird hier ehrlich getrennt.

### 4.1 Core — die Souveränitätsschicht (gebaut)

Der Core ist die heute existierende, in Code und Tests belegte Substanz des Produkts:

- SIP-Signaling (REGISTER, INVITE-Dialog, CANCEL/BYE/ACK/Re-INVITE, Transfer, Redirect)
- RTP / RTCP-Medientransport, SRTP
- Call-Lifecycle, Hold / Transfer / Konferenz-/Bridge-Pfad
- Media-Routing / Cross-Connect, Audio-Devices
- stabile Public API über `VoipClient` / `IVoipClient`

Diese Fähigkeiten sind der belegbare Produktkern. **Reifegrad und Einschränkungen pro
Fähigkeit** (inklusive der bekannten Lücken wie fehlender Interop-/Soak-Nachweis,
transport-only-Video, WebRTC-Browser-Interop) stehen detailliert in der
[Fähigkeiten- und Reifegrad-Matrix](../technical/capabilities-matrix.md). Diese
Positionierungsseite ersetzt diese Matrix nicht und behauptet keinen höheren Reifegrad.

### 4.2 Progressive API — Komfort und kontrollierte Tiefe (gebaut)

Der Core ist nicht nur eine gekapselte High-Level-API. Er stellt drei miteinander
kombinierbare Nutzungstiefen bereit:

| Nutzungstiefe | Öffentliche Oberfläche | Produktnutzen |
|---------------|-------------------------|---------------|
| **Managed Workflows** | `ConnectAsync`, `DialAndWaitUntilConnectedAsync`, Default-Audio, Playback, Recording | wenig Integrationscode für Standardabläufe |
| **Typisierte Call-Steuerung** | `IPhoneLine`, `ICall`, Transfer, DTMF, In-Dialog-Aktionen, ausgehandelte Medien-, Quality- und ICE-Zustände, ausgehende Custom-Header | eigene Routing-, Kampagnen- und Contact-Center-Logik |
| **Medien- und Extension-Seams** | `IMediaReceiver`, `IMediaSender`, `MediaConnector`, eigene Audio-Devices und Telemetrie-Sinks, `ModuleRegistry` | Bots, STT/TTS, Medienrouting, Observability und separate Module |

Die Ebenen schließen einander nicht aus: Ein Produkt kann den komfortablen Dial-Workflow
nutzen und nur für einzelne Calls einen Media-Tap oder tiefere Call-Steuerung ergänzen.
Das ist die belegbare Differenzierung: **einfache Dinge bleiben einfach, anspruchsvolle
Produktlogik bleibt möglich, ohne die SDK zu ersetzen.**

Die Grenze ist ebenfalls Teil der Positionierung. Öffentliche Call-, Medien- und
Extension-Verträge sind unterstützt; interne Transport-, Parser- und Wire-Typen werden
nicht als beliebig manipulierbare Low-Level-API exponiert. Der aktuelle Asterisk-Nachweis
belegt die Kombination aus High-Level-Workflow, `ICall`, Media-Tap/-Injection und Bridge,
nicht jedoch einen vollständigen Escape-Hatch-Vergleich gegen Ozeki oder SIPSorcery.
Technische Details:
[Progressive API](../../portal/concepts/progressive-api.md) und
[Fähigkeiten-Matrix, Abschnitt 12](../technical/capabilities-matrix.md#12-öffentliche-api--facade).

### 4.3 Differenzierungsmodule — **Vision, nicht gebaut**

> **Wichtiger Due-Diligence-Hinweis:** Die vier Differenzierungsmodule sind **strategisches
> Zielbild (Vision), kein vorhandenes Produkt.** Es existiert **kein `src/`-Projekt** für sie.
> Die Capabilities-Matrix führt sie ausdrücklich als **„Nicht gebaut"** — siehe
> [Fähigkeiten- und Reifegrad-Matrix, Abschnitt Differenzierungsmodule](../technical/capabilities-matrix.md).
> Kaufentscheidungen dürfen diese Module **nicht** als vorhandenen Produktwert einpreisen.

Als Vision über dem Core geplant (Phase 3 der CEO-Vision (intern, nicht Teil des Pakets)):

| Modul (Vision) | Geplanter Zweck | Ist-Stand |
|----------------|-----------------|-----------|
| `CalloraVoipSdk.Privacy` | Redaction, Consent, Policy-Gates, Audit | **Nicht gebaut** (Vision) |
| `CalloraVoipSdk.Risk` | Spam-/Scam-Signale, Call-Risk-Screening | **Nicht gebaut** (Vision) |
| `CalloraVoipSdk.Intelligence` | AMD, Sentiment, Transcript, lokale Modelle | **Nicht gebaut** (Vision) |
| `CalloraVoipSdk.Policy` | Tenant-Regeln, Decision-Profiles, Compliance | **Nicht gebaut** (Vision) |

Was **gebaut** ist, ist der **vorgesehene Integrationspunkt**, nicht die Module selbst:
der öffentliche Media-Tap-Contract als sauberer Andockpunkt für spätere Intelligence-/
Risk-Logik. Die Module selbst bleiben Roadmap. Der geplante Vertriebsweg dafür ist der
zentral signierte, kuratierte Modul-Store (First-Party + spätere Community-Vendoren, siehe
[ADR-008](../../adr/ADR-008-community-module-store.md)) — dessen Backend-Ausbau laut ADR-008
selbst ebenfalls **noch nicht gebaut** ist.

Reihenfolge und Zeithorizont der Vision-Ebene: siehe [Roadmap](roadmap.md).
Kommerzielle Verpackung (Tiers, Add-ons, White-Label): siehe [Lizenzmodell](licensing-model.md).

### 4.4 Plattform-Richtung (Vision)

Über SDK und Module hinaus ist eine **host-zentrierte Plattform** vorgezeichnet: schlanke
OSS-Engine + kommerzielle Host-/SaaS-Schicht (Tenants, Entitlements, Plugin-Lifecycle) +
Plugin-Ökosystem — das „Shopware für Voice"-Modell (siehe
[ADR-007](../../adr/ADR-007-host-centric-platform-split.md) und
VOICE_PLATFORM_IDEE (intern)). Auch dies ist **strategische Richtung,
kein ausgeliefertes Produkt** — der kommerzielle Produktwert liegt heute im Core, der
Plattformwert ist Zielbild.

---

## 5. Wettbewerbsposition

### 5.1 Referenzklasse

Der geschäftliche Bezugspunkt ist die **Ozeki-Klasse** kommerzieller VoIP-SDKs:
CalloraVoipSdk ist als kommerzielles SDK positioniert, das im **geschäftlichen Wert**
mit Ozeki-Class-Angeboten vergleichbar ist — stabile, entwicklerfreundliche API für
Telefonie- und Media-Workflows, einbettbar in PBX-, Contact-Center- und
Voice-Automation-Systeme.

Der Ozeki-Vergleich ist eine **Wert- und Kategorie-Referenz** (welche Kundenprobleme
werden abgedeckt, in welcher Preis-/Wertklasse), keine Aussage über
Feature-für-Feature-Parität. Die belegbare Feature-Abdeckung steht in der
[Capabilities-Matrix](../technical/capabilities-matrix.md).

### 5.2 Differenzierung: eigener Stack = Kontrolle, Privacy, EU

Der Unterschied zu Cloud-CPaaS-APIs und zu SDKs, die auf einem Fremd-Stack aufsetzen,
ist der **eigene, souveräne Stack**:

- **Kontrolle** — Signaling und Medienpfad laufen im eigenen Prozess. Der öffentliche
  Vertrag reicht von Managed Workflows über typisierte Call-Steuerung bis zu codierten
  Media-Taps und Extension-Seams. Beliebige Wire-Manipulation ist keine öffentliche
  SDK-Zusage und würde eine Core-Erweiterung erfordern.
- **Privacy** — self-hostbar; keine erzwungene Weitergabe von Gesprächsdaten an eine
  fremde Cloud. Das ist die harte Grundlage der Privacy-Positionierung — und der Grund,
  warum die (noch zu bauenden) Privacy-/Intelligence-Module gerade *hier* Wert schöpfen
  würden: lokale, europäische, datensparsame Verarbeitung im eigenen Prozess statt in
  einer Drittanbieter-Cloud.
- **EU / Souveränität** — europäische Verortung und Self-Hosting adressieren
  Datenresidenz- und Compliance-Anforderungen (DSGVO, Behörden, regulierte Branchen),
  die CPaaS-Modelle mit außereuropäischer Datenhaltung strukturell schwerer erfüllen.
- **Unabhängigkeit** — keine Per-Minute-Abrechnung im Medienpfad, kein Vendor-Lock-in
  auf einen Cloud-Anbieter oder einen fremden SIP-Stack.

Kurzfassung: **Cloud-CPaaS tauscht häufig Bequemlichkeit gegen Kontrolle.
CalloraVoipSdk verbindet einen bequemen Happy Path mit kontrollierter technischer Tiefe,
Privacy und EU-Souveränität als Produktkern.** Für die adressierten Zielkunden
(regulierte Branchen, Voice-AI mit Datenschutzdruck, OEMs, die keinen Lock-in wollen) ist
das der entscheidende Hebel.

---

## Querverweise

- [Fähigkeiten- und Reifegrad-Matrix](../technical/capabilities-matrix.md) — belegbarer Ist-Stand pro Fähigkeit (Grundlage für alle Reifegrad-Aussagen dieser Seite)
- [Roadmap](roadmap.md) — Zeithorizont der Vision-Ebene (Module, Plattform)
- [Lizenzmodell](licensing-model.md) — Tiers, Add-ons, White-Label, Modul-Vertrieb
- [ADR-007 — Host-Centric Platform Split](../../adr/ADR-007-host-centric-platform-split.md)
- [ADR-008 — Community Module Store](../../adr/ADR-008-community-module-store.md)
- CEO-Vision (intern, nicht Teil des Pakets) · Voice-Platform-Idee (intern) — Quellen der strategischen Positionierung
