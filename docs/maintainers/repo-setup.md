# Repository-Setup (GitHub-Weboberfläche)

Diese Schritte lassen sich nur in der GitHub-Weboberfläche erledigen, nicht per Datei im
Repo. Einmalig durchführen; jeder mit Admin-Recht auf das Repository kann es. Reihenfolge
egal. Menüpfade Stand 2026 — falls GitHub Bezeichnungen leicht ändert, steht der Zweck
jeweils dabei.

Repo: `github.com/BechsteinDigital/callora-voip-sdk`

---

## 1. Label-Farben & Beschreibungen setzen (~5 Min)

Die Labels existieren bereits (an den Issues #3–#20 vergeben), sind aber grau und ohne
Beschreibung. Farbe/Text macht den Tracker auf einen Blick lesbar.

**Weg:** Repo öffnen → Reiter **Issues** → Button **Labels** (neben „Milestones"), oder
direkt `…/callora-voip-sdk/labels`.

Für jedes Label: **Edit** → Beschreibung eintragen → im Farbfeld den Hex-Wert (ohne `#`)
eingeben → **Save changes**.

| Label | Farbe | Beschreibung |
|---|---|---|
| `P1` | `b60205` | Interop-/Stabilitätsrisiko — zuerst angehen |
| `P2` | `d93f0b` | Randfall / Härtung |
| `P3` | `fbca04` | Infrastruktur / Aufräumen |
| `area: sip` | `c5def5` | SIP-Signalisierung, Transport, Transaktionen |
| `area: media` | `c5def5` | RTP / RTCP / SRTP / DTLS |
| `area: connectivity` | `c5def5` | STUN / TURN / ICE |
| `area: sdp` | `c5def5` | SDP / Offer-Answer / Datei-Codecs |
| `area: core` | `c5def5` | Application- & Domain-Schicht |
| `area: client-audio` | `c5def5` | Client-Fassade, Audio-Backends, Stats |
| `area: ci-test` | `c5def5` | CI, Tests, Performance, Interop |
| `tracking` | `5319e7` | Meta-/Übersichts-Issue |

Die `area:`-Labels bewusst alle gleiche Farbe — so lesen sie sich als eine Familie. Die
Standard-Labels (`bug`, `enhancement`, `documentation`, `good first issue`, `help wanted`)
sind schon korrekt gefärbt und bleiben unverändert.

## 2. Private Schwachstellenmeldung aktivieren (~1 Min)

Aktiviert den privaten Meldekanal, auf den `SECURITY.md` und die Issue-Vorlage
(`config.yml`) bereits verweisen. Ohne das führt der „Report a security vulnerability"-Link
im Issue-Dialog ins Leere.

**Weg:** Reiter **Settings** → linke Leiste **Code security** (früher „Code security and
analysis") → Abschnitt **Private vulnerability reporting** → **Enable**.

Danach können Melder unter dem Reiter **Security** → **Report a vulnerability** privat
melden; ihr bekommt es als Draft-Advisory, nicht als öffentliches Issue.

## 3. Discussions aktivieren (~1 Min, optional)

`config.yml` verlinkt einen Discussions-Bereich für How-to-Fragen (damit der Issue-Tracker
für echte Bugs/Features frei bleibt). Solange Discussions aus ist, führt dieser Link ins
Leere.

**Weg:** **Settings** → **General** → Abschnitt **Features** → Häkchen bei **Discussions**.

Wollt ihr Discussions (noch) nicht, entfernt stattdessen den entsprechenden `contact_links`-
Eintrag aus `.github/ISSUE_TEMPLATE/config.yml` — dann verschwindet der Link.

## 4. Branch-Protection für `main` (~3 Min, empfohlen)

Verhindert versehentliche Direkt-Pushes und erzwingt grüne CI + Review, bevor etwas nach
`main` gelangt — wichtig, sobald Externe mitarbeiten.

**Weg (Rulesets, GitHubs aktueller Ansatz):** **Settings** → **Rules** → **Rulesets** →
**New ruleset** → **New branch ruleset**.

- **Name:** z. B. `protect-main`
- **Enforcement status:** **Active**
- **Target branches:** **Add target** → **Include default branch** (das ist `main`).
- Regeln (Häkchen setzen):
  - **Require a pull request before merging** — optional „Require approvals: 1".
  - **Require status checks to pass** → **Add checks** und diese drei aus dem CI-Workflow
    auswählen (erscheinen, sobald sie einmal gelaufen sind):
    - `build-and-test (ubuntu-latest)`
    - `build-and-test (windows-latest)`
    - `interop`
  - **Block force pushes**
- **Create**.

> Falls die Status-Checks in der Auswahlliste fehlen: Sie tauchen erst auf, nachdem der
> CI-Workflow mindestens einmal auf einem PR gelaufen ist. Ruleset zunächst ohne die Checks
> anlegen, nach dem ersten PR-Lauf ergänzen.

*(Klassische Alternative: **Settings** → **Branches** → **Add branch protection rule**,
Branch name pattern `main`, dieselben Optionen. Rulesets sind der neuere Weg und flexibler.)*

## 5. Prüfen: Community Standards (~1 Min)

Kontrolle, dass GitHub alle Health-Dateien erkennt (README, LICENSE, CONTRIBUTING,
CODE_OF_CONDUCT, SECURITY, Issue-Templates, PR-Template).

**Weg:** Reiter **Insights** → **Community Standards** → alle Punkte sollten ein grünes
Häkchen tragen, sobald dieser Branch in `main` gemerged ist.

## Nicht vergessen: Repo öffentlich schalten

Falls das Repository noch privat ist, macht es das erst zu „Open Source":
**Settings** → **General** → ganz unten **Danger Zone** → **Change repository visibility**
→ **Make public**. Vorher sicherstellen, dass keine Secrets in der Historie liegen
(NuGet-API-Keys u. Ä. gehören in **Settings → Secrets and variables → Actions**, nie in den
Code).
