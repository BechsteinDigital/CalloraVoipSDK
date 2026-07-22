# Engineering Rules

Dieses Dokument ist die normative Regelbasis, auf die sich `tests/CalloraVoipSdk.ArchitectureTests`
bezieht ("Mechanische Gates für ENGINEERING_RULES.md"). Es wurde am 2026-07-22 aus den
Architektur-Tests und den im Quellcode dokumentierten Konventionen rekonstruiert, nachdem das
Original außerhalb des Repos lag. Referenz des zugrunde liegenden Voll-Audits:
`docs/agent-log/2026-07-08-full-sdk-code-review.md` (liegt nicht im Repo; siehe
`docs/audit/CODE_FINDINGS_REGISTER.md` für die rekonstruierten Marker).

## Baseline-Mechanik (gilt für alle mechanischen Regeln)

Jede Regel wird von `EngineeringRulesTests` über den gesamten Quellbaum geprüft und gegen eine
im Test einkompilierte **Baseline bekannter Altlasten** verglichen. Es gilt:

- **Baselines dürfen nur schrumpfen.** Ein neuer Verstoß schlägt fehl.
- **Veraltete Baseline-Einträge schlagen ebenfalls fehl.** Wer einen Altlast-Eintrag behebt,
  muss ihn aus der Baseline im Test entfernen (`SourceScan.AssertMatchesBaseline`).
- Baseline-Einträge sind repo-relative Dateipfade; die Begründung, warum ein Eintrag (noch)
  akzeptabel ist, steht als Kommentar direkt an der Baseline im Test.

Die Architektur-Gates laufen in CI als eigener Schritt **vor** den übrigen Tests
(`.github/workflows/ci.yml`).

## Mechanisch erzwungene Regeln

### R1 — DDD-Schichtrichtung

- `src/Core/Domain` darf **keine** `using`-Abhängigkeit auf `CalloraVoipSdk.Core.Application`,
  `CalloraVoipSdk.Core.Infrastructure` oder `CalloraVoipSdk.Client` haben.
- `src/Core/Application` darf **keine** `using`-Abhängigkeit auf
  `CalloraVoipSdk.Core.Infrastructure` oder `CalloraVoipSdk.Client` haben.
- Abhängigkeitsumkehr statt Ausnahme: Braucht die Domain etwas aus einer äußeren Schicht,
  definiert sie selbst einen Port (Beispiel: `ICallRegistry` in der Domain, implementiert vom
  Application-`CallManager` — Fix K4).

Baseline: leer. Neue Verstöße sind nicht zulässig.

### R2 — Namespace-Schichtsegment = Ordner-Schicht

Eine Datei unter `Domain/`, `Application/` bzw. `Infrastructure/` MUSS ihr eigenes
Schichtsegment im Namespace tragen und darf kein fremdes tragen. Verboten sind beide
Driftformen:

- **Layer-Omission**: z. B. `CalloraVoipSdk.Core.Security` für eine Datei unter
  `Domain/Security/` (der historische HARD-G1-Drift).
- **Foreign Layer**: z. B. `CalloraVoipSdk.Core.Infrastructure.Media` für eine Datei unter
  `Application/Media/`.

Erlaubt bleibt, dass untergeordnete Ordner in den Elternnamespace klappen, solange das
Schichtsegment erhalten bleibt (z. B. `Signaling/Contracts/` →
`…Infrastructure.Sip.Signaling`).

Baseline: leer.

### R3 — Maximal 1000 Zeilen pro Datei

Gilt für `src/`, `tests/` und Beispiele. Übergroße Dateien werden in Kollaborator-Klassen
zerlegt (Vorbild: Aufspaltung der Dialog-Dateien, u. a. `SipForkedInviteHandler`).

Baseline: leer.

> Hinweis: Der Test scannt derzeit `samples/`, das Verzeichnis heißt aber `examples/` —
> Beispiele werden faktisch nicht erfasst (bekannte Kleinstlücke, siehe Tiefenanalyse
> 2026-07-22, Befund 9 im Test-Kapitel).

### R4 — Keine privaten/protected verschachtelten Typen

`private`/`protected class|interface|record` innerhalb anderer Typen ist in `src/` verboten.
Hilfstypen werden Top-Level-`internal` in eigener Datei (Vorbild: `MediaActivity`,
`LearnedPublicContact`).

Baseline: leer.

### R5 — Kein stummer `catch`

Ein `catch`-Block, dessen Body leer ist oder nur aus Kommentaren besteht, ist verboten —
jeder Catch loggt oder behandelt sichtbar. Ein Catch mit Logging fällt automatisch aus der
Verstoßliste.

Baseline: 22 inventarisierte Altlasten (Stand 2026-07-08), überwiegend legitime
Shutdown-/Fallback-Catches auf Dispose-/Transportpfaden. Die Liste steht in
`EngineeringRulesTests.SilentCatchBaseline` und darf nur schrumpfen.

### R6 — Kein Sync-over-Async im Produktcode

`.GetAwaiter().GetResult()` ist in `src/` verboten. Ausnahme sind Dispose-/Transportpfade, in
denen `IDisposable` kein `await` erlaubt — diese sind einzeln in
`EngineeringRulesTests.SyncOverAsyncBaseline` inventarisiert (4 Einträge) und pro Eintrag
review-pflichtig.

## Konventionen (nicht mechanisch erzwungen, aber verbindlich)

Diese Regeln sind durchgängig im Code dokumentiert und werden im Review erwartet; Verstöße
gelten als Fehler, auch wenn kein Gate sie fängt.

### K1 — Fail-closed bei Medien-Sicherheit

Kein Klartext-Downgrade: Ist SRTP/DTLS ausgehandelt oder per Policy gefordert, wird bei
fehlendem/ungültigem Schlüsselmaterial verworfen bzw. abgelehnt (488), nie unverschlüsselt
gesendet oder empfangen ("keyless secure negotiation" terminiert). Sends vor
Schlüsselinstallation werden unterdrückt und gezählt, nicht gepuffert.

### K2 — Enricher-Reihenfolge ist eine Invariante

`CallMediaParameters` wird immutabel in fester Reihenfolge angereichert:
ICE → SRTP → DTLS (`CallMediaParametersIceEnricher` → `…SrtpEnricher` → `…DtlsEnricher`).
Jeder Enricher bewahrt die Felder der Vorgänger; Änderungen an Parametern nach ICE-Selektion
laufen über `with`-Klone, nie über Handkopien (HARD-R5).

### K3 — Threading-Verträge

- Domain-/SDK-Events feuern synchron auf SDK-Threads (SIP-Signaling bzw. Media/RTCP);
  Handler dürfen weder blockieren noch werfen. Event-Dispatch snapshotted den Delegaten
  **innerhalb** des Locks; Invocation läuft außerhalb.
- Auf dem Medien-Hotpath: keine Locks über Fremdcode, keine Allokationen, wo vermeidbar
  (HARD-F1), bounded Buffer mit Drop-Oldest statt unbegrenzter Queues (HARD-F4),
  Copy-on-write-Arrays für Tap-/Listener-Listen.
- Paarige Zustände werden über atomare Snapshot-APIs gelesen/geschrieben, nie feldweise
  (HARD-C1/C2: `AdvertisedPublicContact`, `ActiveInvite`).
- Durchgängig `ConfigureAwait(false)`; `TaskCompletionSource` mit
  `RunContinuationsAsynchronously`; Fire-and-forget nur mit Fault-Beobachtung
  (Continuation + Log).
- Dispose ist idempotent (`Interlocked.Exchange`-Guard) und cancellation-getrieben.

### K4 — Fehlerbehandlung an Vertrauensgrenzen

- Untrusted Remote-Input (SIP/SDP/STUN/RTP-Wire) wird per `Try*`/null-Vertrag geparst —
  Decode wirft nicht, malformte Pakete werden geloggt und verworfen (HARD-G3:
  Parse-Fehlschläge sind observierbar, nie stumm).
- Server-Seiten verwerfen Auth-Fehler still (Amplification-Schutz); Clients kapseln
  Transportfehler in typisierte Exceptions.
- DoS-Kappen an jeder Wire-Grenze (Nachrichtengrößen, Attribut-Anzahl, Frame-Limits,
  Verbindungs-Slots) sind Pflicht für neue Parser/Listener.

### K5 — Secrets

Schlüsselmaterial (SDES-Keys, ICE-Passwörter) erscheint nie in Logs (`SipWireTraceLogger`
redigiert; `CallMediaParameters.ToString()` ist geschwärzt). Abgeleitete Session-Keys werden
bei Dispose genullt (`CryptographicOperations.ZeroMemory`); Integritätsvergleiche
konstantzeitig (`FixedTimeEquals`).

### K6 — Marker statt TODO

`TODO`/`FIXME`/`HACK`-Kommentare sind unerwünscht. Offene Punkte werden als strukturierte
Follow-up-Prosa mit Begründung dokumentiert ("DECISION", "Limitation", "follow-up");
behobene Findings tragen ihren Marker (CF-xxx, HARD-xxx, ADR-xxx) direkt am Code. Das
Marker-Register liegt in `docs/audit/CODE_FINDINGS_REGISTER.md`.

### K7 — RFC-Verweise

Protokollverhalten wird mit RFC-Nummer und Paragraph im Kommentar belegt; bewusste
Abweichungen werden als solche markiert und begründet (Beispiel: `MinSequential=1` statt 2
in `RtpSequenceValidator` mit Begründung).

### K8 — Tests folgen dem Ebenenmodell L0–L4

Neue Funktionalität wird auf der niedrigsten sinnvollen Ebene getestet
(L0 Wire → L1 Security → L2 Media → L3 Signaling → L4 Facade/Interop; Definition in
`docs/audit/2026-07-21-interop-soak-audit-design.md`). Kategorisierung über xUnit-Traits:
`SoakShort` (PR-CI), `SoakLong` (nightly), `Interop` (Docker). Drift-Guards per Reflection
(z. B. `VoipOptionsMappingCompletenessTests`) sind das bevorzugte Mittel gegen schleichende
API-/Mapping-Erosion.
