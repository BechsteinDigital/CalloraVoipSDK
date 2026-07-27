# F&E-Tiefe & Compliance-Haltung — Wertsignal

> Teil des kommerziellen Due-Diligence-Pakets · Stand: 2026-07-27

Dieses Dokument verdichtet **zwei qualitative Wertsignale** des CalloraVoipSdk für die
kaufinteressierte Seite:

1. dass eine **substanzielle, nachvollziehbar dokumentierte Eigenentwicklung** vorliegt —
   ein von Grund auf selbst gebauter Protokoll-Stack mit RFC-belegten Entscheidungen,
   Findings-/Evidenz-Disziplin und einer systematischen Test- und Audit-Kultur; und
2. dass **DSGVO und EU AI Act strukturiert als Baseline adressiert** sind und das Produkt
   architektonisch auf Privacy-by-Design ausgerichtet ist.

Es handelt sich ausdrücklich um **Wert- und Ausrichtungssignale**, nicht um einen
Konformitäts- oder Zertifizierungsnachweis. Wo dieses Paket eine qualitative Aussage trägt,
steht sie; wo nur ein interner Nachweis existiert, wird darauf verwiesen, ohne ihn hier
auszubreiten.

**Abgrenzung — was dieses Dokument bewusst nicht enthält:**

- keine Finanz-, Steuer-, Förder- oder Aufwandszahlen jeglicher Art;
- keine Rohunterlagen aus der F&E-Förderakte oder der Compliance-Ablage;
- keine Konformitäts-, Prüfvermerk- oder Zertifizierungsbehauptung.

Die detaillierten F&E-Förderunterlagen und die Compliance-Rohdokumente liegen **intern** und
sind **nicht Teil dieses öffentlichen Übergabepakets**. Sie werden — falls überhaupt — nur auf
Anfrage, in einer späteren Due-Diligence-Phase und unter NDA offengelegt.

---

## 1. F&E-Tiefe als Asset

Der zentrale Wert des Projekts ist kein zusammengesetztes Fremdbibliotheks-Bündel, sondern
ein **eigener SIP/RTP/SRTP/DTLS/ICE-Stack, von Grund auf entwickelt**. Diese Tiefe ist nicht
nur behauptet, sondern über den Entwicklungsverlauf **belegt und rekonstruierbar** — genau das
macht sie zu einem prüfbaren Asset statt zu einer bloßen Aussage.

### 1.1 Nachvollziehbarer Entwicklungsverlauf

- **61 Architecture Decision Records** halten die tragenden Architekturentscheidungen fest —
  jeweils im Schema *Context → Decision → Consequences → Guardrails*. Sie decken den ganzen
  Stack ab: SIP-Signaling und Dialog-Lebenszyklus, SDP-Aushandlung, SRTP/DTLS-SRTP-Keying und
  Fail-closed-Härtung, RTCP/RTX/Transport-CC, ICE/TURN-Relay, Video-Transport, Codec-Integration,
  Medien-Hotpath-Concurrency sowie die Liefer- und Audit-Prozesse selbst.
- Die ADR-Historie ist **ehrlich geführt**: Die erste Reihe wurde original verfasst, die
  spätere Reihe aus der dokumentierten Entwicklungshistorie backfilled und **gegen den echten
  Quellcode verifiziert**. Wo ein Log mehr behauptete, als der Code liefert, hält der ADR den
  realen Stand fest und weist die Abweichung offen aus. Zwei ADRs tragen datierte Errata.
- Übersicht und Einstieg: [`../../adr/README.md`](../../adr/README.md).

Das Signal für einen Erwerber: Die Entscheidungen hinter dem Code sind dokumentiert, begründet
und an den Quellcode rückgebunden. Wissen steckt nicht ausschließlich in Köpfen, sondern ist
übergabefähig festgehalten.

### 1.2 RFC-belegte Entscheidungen

Protokollverhalten wird nicht „nach Gefühl" implementiert, sondern **mit RFC-Nummer und
Paragraph im Code belegt**; bewusste Abweichungen von einem RFC werden als solche markiert und
begründet (Engineering-Regel K7). Der Stack folgt damit nachvollziehbar den einschlägigen
Standards — u. a. RFC 3261 (SIP), die SDP-/SRTP-/DTLS-SRTP-Familie, RFC 8445/8656 (ICE/TURN)
und die RTP/RTCP-Feedback-Mechanik.

Der Wert liegt in der **Interop-Erwartbarkeit**: Ein RFC-verankerter Stack ist gegenüber
fremden SIP-/WebRTC-Gegenstellen belastbarer und für Käufer besser einschätzbar als eine
undokumentierte Eigeninterpretation.

### 1.3 Findings- und Evidenz-Disziplin

Das Projekt behandelt Qualität als **buchführungspflichtig**:

- **Marker statt TODO** (Regel K6): Offene Punkte werden als strukturierte Follow-up-Prosa mit
  Begründung geführt, nicht als verstreute `TODO`/`FIXME`. Behobene Findings tragen ihren
  Marker (CF-xxx, HARD-xxx, ADR-xxx) direkt am Code; das Register ist die dauerhafte
  Audit-Erinnerung.
- **Claim-Disziplin**: Der durchgehende Projektgrundsatz lautet „Doku ≤ Nachweis" —
  Status-Aussagen wie *fertig*, *vollständig* oder *compliant* sind nur mit direktem Nachweis
  durch Code, Tests und Scope-Abdeckung zulässig. „Testgrün allein" gilt ausdrücklich **nicht**
  als Beweis für eine Status-Hochstufung.

Für einen Erwerber ist das doppelt wertvoll: Die bekannten offenen Punkte sind **explizit und
auffindbar** statt versteckt, und die Aussagen in der Übergabe-Doku sind kalibriert statt
geschönt.

### 1.4 Systematische Test- und Audit-Kultur

Die Teststrategie folgt einem **Ebenenmodell L0–L4** (Wire → Security → Media → Signaling →
Facade/Interop): Fehler werden auf der Ebene isoliert, auf der sie entstehen. Architektur- und
Regel-Gates laufen **mechanisch in CI** und erzwingen die tragenden Engineering-Regeln
(Schichtrichtung, Dateigrößen, kein stummer `catch`, kein Sync-over-Async u. a.) gegen
Shrink-only-Baselines — Regressionen schlagen automatisch fehl.

Details, belegte Testprojekte, CI-Gates und die bewusst offen ausgewiesenen Testlücken:
[`../technical/quality-and-testing.md`](../technical/quality-and-testing.md).

Zusammengenommen ergeben ADRs, RFC-Belege, Findings-Register und Ebenen-Tests ein **kohärentes
Nachweisgeflecht**: Die F&E-Tiefe ist nicht nur vorhanden, sie ist prüfbar und übergabefähig
dokumentiert.

---

## 2. Compliance-Haltung (Baseline & Ausrichtung)

Compliance ist im Projekt als **strukturierte Baseline und als Produktausrichtung** angelegt —
nicht als abgeschlossener Konformitätsnachweis. Diese Einordnung ist bewusst ehrlich: Es gibt
eine dokumentierte Haltung und architektonische Vorkehrungen, aber **keine Zertifizierung** und
keinen Konformitäts-Prüfvermerk. Die Baseline selbst bezeichnet sich als technische Grundlage
und ausdrücklich **nicht als Rechtsberatung**.

### 2.1 DSGVO / EU AI Act — strukturiert adressiert

Eine plattformweite Compliance-Baseline hält als Engineering-Vorgabe qualitativ fest:

- **DSGVO-Richtung**: Privacy-by-Design und Privacy-by-Default als verbindliche
  Architekturprinzipien; Datenminimierung und Zweckbindung je Datenfluss; vorgesehene
  Betroffenenrechte (Export-/Löschpfade); Aufbewahrungs-/Retention-Policies je Datenklasse;
  Audit-Trails für sicherheits- und compliance-relevante Aktionen; EU-Datenresidenz für den
  Cloud-Betrieb.
- **EU-AI-Act-Richtung**: ein AI-Feature-Register mit Risikoeinstufung, Zweck und Modellquelle;
  Human-Oversight mit Eingriffs-/Override-Möglichkeit; Transparenz über KI-Beteiligung;
  revisionssichere Traceability von Modell/Prompt/Policy; Release-Safety-Gates.

Diese Punkte sind als **Baseline und Definition-of-Done-Richtung** formuliert, d. h. als
strukturierter Rahmen für künftige Features — nicht als Behauptung, dass jeder Pfad bereits
vollständig implementiert und abgenommen ist. Genau so ist das Signal zu lesen: **Die
Compliance-Fragen sind früh und systematisch gestellt und mit einem Architekturrahmen
beantwortet.**

### 2.2 Privacy-by-Design als europäische Ausrichtung

Die Produktvision positioniert das SDK als **europäische, souveräne B2B-Voice-Runtime** mit
voller technischer Kontrolle über Telefonie, Medienpfad und Privacy. Kennzeichnend sind:

- **datensparsame, lokale Pfade** als bewusste Ausrichtung (z. B. lokale/europäische
  Verarbeitungswege statt zwingender Cloud-Auslagerung);
- eine geplante **Differenzierung über Privacy/Policy/Consent** als eigenständige Module
  oberhalb des Kerns;
- Sicherheits-Grundsätze, die diese Haltung technisch stützen: Fail-closed bei
  Medien-Sicherheit (kein Klartext-Downgrade), Schwärzung von Schlüsselmaterial in Logs und
  Nullen abgeleiteter Session-Keys bei Dispose (Engineering-Regeln K1/K5).

Dies ist als **Vision und Ausrichtung** einzuordnen, teils bereits im Kern verankert (die
Sicherheits-Grundsätze sind mechanisch bzw. review-verbindlich durchgesetzt), teils
Roadmap-Richtung für die Privacy-/Policy-Module. Der Wert für einen europäischen B2B-Käufer:
eine glaubwürdige, datenschutzorientierte Grundausrichtung statt eines nachträglich
aufgesetzten Compliance-Anstrichs.

---

## 3. Einordnung des Signals (Doku ≤ Nachweis)

Damit dieses Wertsignal nicht stärker gelesen wird, als es belegt ist:

- **Belegt und prüfbar**: die F&E-Tiefe (Stack-Eigenentwicklung, 61 ADRs, RFC-Belege,
  Findings-Register, L0–L4-Tests mit CI-Gates). Der Detailnachweis steht in
  [`../technical/quality-and-testing.md`](../technical/quality-and-testing.md) und
  [`../../adr/README.md`](../../adr/README.md).
- **Baseline/Ausrichtung, nicht Nachweis**: die Compliance-Haltung. Vorhanden sind eine
  strukturierte DSGVO-/EU-AI-Act-Baseline und eine Privacy-by-Design-Ausrichtung — **keine**
  Zertifizierung, kein Konformitäts-Prüfvermerk, keine Zusicherung vollständiger Umsetzung
  aller Pfade.
- **Bewusst nicht Teil dieses Pakets**: F&E-Förderunterlagen und Compliance-Rohdokumente
  (intern; nur auf Anfrage / spätere DD-Phase / NDA).

Das Signal, verdichtet: **substanzielle, dokumentierte und rückgebundene Eigenentwicklung** plus
eine **früh und strukturiert eingenommene Compliance-Haltung** — beides ehrlich als das
ausgewiesen, was es ist.
