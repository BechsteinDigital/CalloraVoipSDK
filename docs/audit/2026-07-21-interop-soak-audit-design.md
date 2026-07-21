# Spec: Interop- + Soak-Test-Kampagne für den Non-WebRTC-Stack

**Status:** Entwurf zur Freigabe · **Datum:** 2026-07-21 · **Branch/Worktree:** `worktree-feat+interop-soak-audit` (`.claude/worktrees/feat+interop-soak-audit`, von `origin/main` @ `dc708d0`)

## 1. Kontext & Ziel

Der eigene SIP/RTP/SRTP/SDP/Audio-Stack (ohne WebRTC) soll systematisch unter zwei Blickwinkeln geprüft werden:

- **Soak** — Stabilität über Zeit/Last (Leaks, Qualitäts-Drift, Concurrency, Langzeit-Signaling).
- **Interop** — Wire-Konformität gegen reale Fremd-Stacks.

Ist-Zustand (verifiziert):
- `tests/CalloraVoipSdk.SoakTests` existiert, ist aber **leer** (nur `Skeleton_IsWired`).
- `tests/CalloraVoipSdk.Core.IntegrationTests` deckt SIP/RTP/SRTP/SDP/ICE/RTCP tief ab — aber **in-process gegen den eigenen Stack und Fakes**; keine echte Fremd-Interop, kein Dauerlauf-Harness.
- Baseline-Build im Worktree: **0 Warnungen / 0 Fehler** (net8/net9/net10, 47 s).

## 2. Ziel-Artefakt: das Audit-Register

Der **eigentliche Deliverable** dieser Kampagne ist ein lebendes Register, nicht der Testcode allein.

- Ort: `docs/audit/INTEROP_SOAK_AUDIT.md` (versioniert via gezielter `.gitignore`-Ausnahme `!docs/audit/`, analog zum bestehenden `!docs/portal`).
- Jeder Test-/Soak-Lauf, der abweicht, erzeugt einen Eintrag mit:

| Feld | Inhalt |
|---|---|
| `FID` | Fortlaufende Finding-ID |
| Evidenz | Test/Szenario + Gegenstelle, das es reproduziert *(a: Fehler)* |
| Symptom | Beobachtung (Wire-Abweichung, Leak-Trend, Media-Defekt) |
| Fehlerquelle | Root-Cause-Kategorie *(b: Fehlerquelle)* |
| Fundstelle | Betroffene `Datei:Zeile` im SDK *(c: Zeilen-Analyse)* |
| Fix-Vorschlag | Was zu ändern wäre — **als Vorschlag, nicht ausgeführt** |
| Schweregrad / Status | dokumentiert / offen |

## 3. Nicht-Ziele (harte Grenzen)

- **Kein autonomes Bugfixing.** Dieses Paket produziert ausschließlich Tests + Dokumentation. Jeder SDK-Fix ist ein **separates, eigens freigegebenes Paket**.
- **Kein WebRTC** in dieser Kampagne (SIP-Video zählt dazu, WebRTC/BUNDLE/DataChannel nicht).
- **Kein stiller Scope-Einbau** — neue Folgearbeit wird als Finding/Backlog notiert, nicht mitgebaut (ENGINEERING_RULES).

## 4. Architektur

```text
tests/
  CalloraVoipSdk.InteropHarness/     NEU  gemeinsames Fundament
  CalloraVoipSdk.SoakTests/          befüllen (referenziert Harness)
  CalloraVoipSdk.InteropTests/       NEU  gegen Docker-Peers + Fritzbox
docker/interop/                      NEU  vorkonfigurierte Peer-Configs
docs/audit/INTEROP_SOAK_AUDIT.md     NEU  lebendes Register
```

**`InteropHarness`** (protokoll-nah, keine Schichtverletzung, DI, thread-safe by design):
- **Call-Harness**: zwei SDK-Instanzen (UAC↔UAS) über echten UDP-Loopback, steuerbarer Lebenszyklus (Register → INVITE/200/ACK → RTP-Audio → BYE), beide Enden inspizierbar.
- **Metrik-Sampler**: RAM/Handles/Threads/Sockets + RTCP-Qualität, mit **Trend-Asserts** statt Momentaufnahme.
- **Szenario-Bausteine** + **Media-Verifier** (siehe §7).
- **Audit-Sink**: strukturierter Roh-Befund → Register.

## 5. Ausführungsstrategie

| Gegenstelle | Mechanismus | CI |
|---|---|---|
| Asterisk | Testcontainers-für-.NET, Image via GHCR-Mirror | **ja** |
| FreeSWITCH | Testcontainers-für-.NET, Image via GHCR-Mirror | **ja** |
| 3CX | docker-compose (Lizenz-Aktivierung nötig), env-gated | opt-in/lokal |
| Fritzbox | reales Gerät, env-gated, Credentials via User-Secrets | nein (nur lokal) |

- CI: GitHub Actions, public repo → **keine Minuten-Kosten** auf Standard-Runnern. Docker-Hub-Rate-Limits über GHCR-Spiegelung umgangen.
- Test-Kategorien über xUnit-Traits gated: `Loopback` (immer/CI), `Interop-Docker` (CI), `Interop-External` (3CX/Fritzbox, opt-in), `Soak-Long` (nur lokaler Langlauf). CI fährt `Loopback` + `Interop-Docker` + Soak-Kurzprofil.

## 6. Test-Matrix (Peers × Features)

Feature-Schichten, pro Gegenstelle inkrementell:
1. **Basis-Telefonie** — REGISTER, INVITE/200/ACK, RTP-Audio (G.711/Opus), BYE.
2. **Mid-Call** — Hold/Resume (re-INVITE), DTMF (RFC 2833 + SIP INFO), Session-Timers, Transfer (REFER).
3. **Transport + Sicherheit** — UDP/TCP/TLS-Signaling, SRTP/SDES.
4. **Video-über-SIP** — Codec-Negotiation, soweit Gegenstelle es kann.

## 7. Media-Verifikation (pro Peer-Klasse)

- **Loopback**: direkter Vergleich gesendeter/empfangener Frames an beiden Enden.
- **Asterisk/FreeSWITCH**: `Echo()`/`Playback(Ton)`-Dialplan → RTP-Round-Trip bzw. bekannter Ton wird empfangen und geprüft (Energie/Frequenz).
- **3CX/Fritzbox**: zwei Nebenstellen, SDK↔PBX↔SDK.

## 8. Soak-Design

Vier Fokusse (alle gewählt), primär über den **deterministischen Loopback**; zwei Profile: **CI-Kurzlauf** (Minuten) + **lokaler Langlauf** (Stunden/Nacht).

| Fokus | Metrik / Erfolgskriterium |
|---|---|
| Ressourcen-Leaks | RAM/Handle/Socket/Thread-Sockel stabil, kein monotones Wachstum über N Calls |
| Media-Qualität-Drift | Jitter/Loss/Clock-Drift/RTCP über Stunden-Call (opt. Netzemulation), kein Degradieren |
| Last / Concurrency | viele parallele Calls: keine Deadlocks/Races, Fehler-/Timeout-Rate flach |
| Langzeit-Signaling | Re-REGISTER-Zyklen, Session-Timer-Refreshes, lange Idle-Dialoge stabil, kein Silent-Drop |

## 9. Slice-Plan

Jede Slice = eigener Commit auf dem Paket-Branch, Review pro Slice; jede Phase speist das Audit-Register.

- **Phase 0 — Fundament**
  - 0.1 `InteropHarness`: Loopback-Call-Harness (2 Instanzen, UDP, INVITE→RTP→BYE)
  - 0.2 Metrik-Sampler + Trend-Assert-Primitiven
  - 0.3 Audit-Register-Gerüst + Finding-Workflow · (Vorab prüfen: ist `SoakTests` in `CalloraVoipSdk.sln`?)
- **Phase 1 — Soak über Loopback**: 1.1 Leaks · 1.2 Qualitäts-Drift · 1.3 Concurrency · 1.4 Langzeit-Signaling
- **Phase 2 — Interop Asterisk** (vertikaler Durchstich + Matrix-Vorlage): Testcontainer/CI → Basis → Mid-Call → Transport/Security → Video
- **Phase 3 — Interop FreeSWITCH** (wie Ph. 2)
- **Phase 4 — Interop 3CX** (Compose, opt-in)
- **Phase 5 — Interop Fritzbox** (real, lokal, User-Secrets)

## 10. Voraussetzungen vom User (später, für externe Ebene)

- 3CX: Lizenz-Aktivierung (kostenlose Edition genügt vermutlich; Grenzen prüfen).
- Fritzbox: eingerichtete IP-Nebenstelle(n) + Zugangsdaten (User-Secrets), lokaler Netzwerkzugang.

## 11. Regel-Bezug

- ENGINEERING_RULES: DDD-Schichten sauber, keine verschachtelten Typen, ≤1000 Zeilen/Datei, TDD-orientiert, kein stiller `catch`, Thread-Safety by design, DI.
- Scope-/Claim-Regeln: kein DONE-/Compliance-Claim ohne Evidenz; Doku behauptet nie stärker als der Nachweis.
- Dies ist ein **neues PO-Paket** außerhalb des aktuellen STATE.json-Scopes (P0 = CORE-011).

## 12. Nächster Schritt

Nach Freigabe dieses Specs → Implementierungsplan für **Phase 0** (writing-plans).
