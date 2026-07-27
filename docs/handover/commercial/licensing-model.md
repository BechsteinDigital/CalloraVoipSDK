# Lizenz- und Monetarisierungsmodell

> **Teil des kommerziellen Due-Diligence-Pakets.**
> Stand: 2026-07-27

Dieses Dokument beschreibt das angedachte Lizenz- und Monetarisierungsmodell des
CalloraVoipSdk sowie die technische Grundlage, auf der es aufsetzt. Es trennt bewusst
zwischen **Modell/Richtung** (strategische Absicht, noch nicht verbindlich) und
**belegten technischen Fakten** (im Code oder in Konfiguration nachweisbar).

> **Wichtiger Hinweis — keine Preisliste:** Die hier genannten Lizenzstufen und Zusatzangebote
> beschreiben eine **Richtung/Vision**, keine verbindliche Angebots- oder Preisliste. Preise,
> Staffelungen und konkrete SLA-Zusagen sind Vertragsgegenstand und hier bewusst nicht
> beziffert.

---

## 1. Lizenzstufen (Richtung / Vision)

Quelle: interne CEO-Vision (auf Anfrage/NDA), Abschnitt „Lizenzmodell (Richtung)".
Dies ist die strategische Ausrichtung, **nicht** ein aktiv verkauftes Preismodell.

| Stufe | Zielgruppe (angedacht) | Charakter |
|-------|------------------------|-----------|
| **Developer / Starter** | Einzelentwickler, Evaluierung, kleine Integrationen | Einstieg, Erprobung des Stacks |
| **Commercial** | Produktteams, die das SDK in ein kommerzielles Produkt einbetten | Produktiver kommerzieller Einsatz |
| **OEM / Enterprise** | PBX-/UC-Anbieter, Contact-Center-Hersteller, Plattform-Embedder | Einbettung/Weitergabe im großen Maßstab |

**Zusatzangebote (angedacht):**

- **Maintenance** — laufende Pflege / Updates
- **Support / SLA** — reaktionszeit-gebundener Support
- **Integrationsberatung** — projektbezogene technische Begleitung
- **Premium-Module** — kostenpflichtige Differenzierungsmodule (siehe §2)
- **White-Label** — Auslieferung unter fremder Marke

Der Produktwert ist gemäß **ADR-007** klar verortet: Die Engine (Telephony-Kern:
SIP/RTP/RTCP/SRTP/Media) ist als offene, schlanke Basis gedacht; der monetarisierbare
Produktwert liegt im **Host + Plugins/Module**. Cloud und Self-hosted nutzen denselben
Plattformkern. → `../../adr/ADR-007-host-centric-platform-split.md`

> **Einordnung:** Die vier Differenzierungsmodule aus der Vision
> (`Privacy`, `Risk`, `Intelligence`, `Policy`) sind das strategische Fundament der
> Premium-Modul-Idee. Ihr jeweiliger Reifegrad ist getrennt zu bewerten und wird hier
> **nicht** als fertig behauptet.

---

## 2. Modul- und Store-Monetarisierung

### 2.1 Modul-Registry im SDK — **belegt (real, in diesem Repository)**

Die technische Grundlage für Modul-Monetarisierung ist im SDK vorhanden und getestet:

- **Offener Erweiterungspunkt** — `IVoipClientModule`
  (`src/Client/Application/Modules/IVoipClientModule.cs`).
- **Neutrale Registry** — `ModuleRegistry` / `IModuleRegistry`
  (`src/Client/Application/Managers/ModuleRegistry.cs`,
  `src/Client/Application/Managers/IModuleRegistry.cs`).
  Die Registry prüft **keine** Lizenz; jedes Modul, das `IVoipClientModule` implementiert,
  lädt ohne Lizenz.
- **Test-Nachweis** —
  `tests/CalloraVoipSdk.Client.Tests/ModuleRegistryTests.cs` sowie
  `tests/CalloraVoipSdk.Core.IntegrationTests/VoipClientModuleRegistrationSafetyTests.cs`.

Damit ist das SDK **marketplace-ready** auf Contract-Ebene: Ein kommerzielles Modul kann
sich freiwillig gegen eine Lizenz absichern, ein freies/OSS-Modul lädt ohne Lizenz — ohne
Änderung am Modul-Contract.

### 2.2 Signierte Lizenzen und Community Module Store — **Konzept + Cross-Repo, teils Platzhalter/Demo**

Der Zielzustand ist ein kuratierter, zentral signierter Marktplatz (Modell vergleichbar mit
Apple / Shopware): Ein Store signiert Lizenz-Tokens mit einem Schlüssel; jedes Modul
validiert gegen den im SDK eingebetteten Public Key. Design, Bedingungen und Build-Reihenfolge
sind in **ADR-008** festgehalten. → `../../adr/ADR-008-community-module-store.md`

**Ehrlicher Ist-Stand:**

- Die **Modul-Registry** (§2.1) ist real und in diesem SDK-Repository nachweisbar.
- Die in ADR-008 referenzierten **Lizenz-Signierungs-Typen** (`CalloraVoipSdk.Licensing.*`,
  z. B. `LicensedVoipClientModule`, `SignedLicenseTokenService`, `LicenseValidator`) sind in
  **diesem Repository nicht enthalten** — sie gehören zum getrennten Store-/Callora-Umfeld.
- Das **Store-Backend** (`store-backend/`, statischer Storefront `website/store/`) ist
  **nicht** Teil dieses Repos und nach aktuellem Stand als **Platzhalter / Demo-Mode**
  einzuordnen: Es ist **kein fertiges, produktives Zahlungssystem**. ADR-008 selbst hält fest:
  „Nothing here is built yet. This ADR is the agreed concept."
- Die skizzierte **Stripe-Connect-Marketplace-Abwicklung**, das persistente Katalog-/
  Publisher-Modell, die Review-/Signatur-Pipeline und Lizenz-Revocation sind **geplant**,
  nicht implementiert.

> **Zusammengefasst:** Der *SDK-seitige Andockpunkt* für Modul-Monetarisierung existiert real
> und ist so entworfen, dass Dritt-Module ohne Contract-Bruch aufgenommen werden können. Die
> *kommerzielle Store-Maschinerie* (Signierung, Bezahlung, Fulfillment) ist Konzept-/Demo-Stand
> und lebt außerhalb dieses Repositories.

---

## 3. Technische Lizenz-Grundlage

### 3.1 SDK-Lizenz — **Apache-2.0 (belegt)**

- Das SDK steht unter der **Apache License, Version 2.0**.
  Quelle: `LICENSE` im Repository-Root (Copyright 2026
  Bechstein.Digital Ecommerce UG (haftungsbeschränkt)).
- Die Lizenzdatei wird **in jedes NuGet-Paket mitverpackt**:
  `src/Directory.Build.props` setzt `<PackageLicenseFile>LICENSE</PackageLicenseFile>` und
  packt `LICENSE` sowie `README.md` in jedes Paket (`Pack="true"`).

**Relevanz für Käufer:** Apache-2.0 ist eine permissive Lizenz und erlaubt kommerzielle
Nutzung, Modifikation und Weitergabe. Das oben skizzierte kommerzielle Lizenzstufen-Modell
(§1) ist damit **kein technischer Kopierschutz auf dem Engine-Kern**, sondern eine
**produkt- und vertragsseitige** Monetarisierung (Host, Premium-Module, Support/SLA,
White-Label). Die technische Zugangskontrolle für bezahlte Bausteine erfolgt — dem Modell
nach — über **signierte Modul-Lizenzen** (§2.2), nicht über die Engine-Lizenz selbst.

### 3.2 Verteilung über NuGet — **belegt**

- Auslieferung als **NuGet-Pakete**; `PackageId` = Projektname
  (`<PackageId>$(MSBuildProjectName)</PackageId>`), d. h. je Modul ein eigenes Paket
  (`CalloraVoipSdk.Core`, `CalloraVoipSdk.Audio.Windows`, `CalloraVoipSdk.Audio.Linux`, …).
- **Symbolpakete** werden als `.snupkg` mit derselben Version ausgeliefert
  (`<IncludeSymbols>true</IncludeSymbols>`, `<SymbolPackageFormat>snupkg</SymbolPackageFormat>`).
- Source Link / Repository-Metadaten sind aktiviert (`PublishRepositoryUrl`,
  `EmbedUntrackedSources`).
  Quelle: `src/Directory.Build.props`.

### 3.3 Versionierung & Kompatibilität — **belegt**

- **SemVer** (`MAJOR.MINOR.PATCH`) für alle `CalloraVoipSdk.*`-Pakete; vor dem ersten
  stabilen Release `0.x`-Charakter, erstes stabiles Contract-Release `1.0.0`.
  → `../../reference/semver-policy.md`
- **Alle Kernpakete werden pro Release auf dieselbe Version gesetzt**; Symbole (`.snupkg`)
  tragen dieselbe Version.
- **Aktueller Stand:** `4.6.0-preview.2` (`src/Directory.Build.props`,
  `VersionPrefix=4.6.0`, `VersionSuffix=preview.2`). Release-Versionen kommen im
  Release-Workflow aus dem Git-Tag (`/p:Version=X.Y.Z`).
- **Release-Kanäle:** Stable `x.y.z`, Preview `x.y.z-preview.n`, RC `x.y.z-rc.n`.
- **Breaking-Change-Governance:** Definition von Breaking Changes, `[Obsolete]`-Deprecation
  (mindestens ein Minor-Zyklus vor Entfernung) und verpflichtendes `CHANGELOG.md`.
  → `../../adr/ADR-006-api-versioning-strategy.md`

> **Caveat aus ADR-006 (Errata 2026-07-27):** Ein ursprünglich vorgesehenes automatisiertes
> Public-API-Surface-Gate (`PublicApiSurfaceTests` gegen `PublicApi.approved.txt`) ist
> **nicht implementiert**. API-Kompatibilität wird heute durch **Review +
> `[Obsolete]`-Disziplin + CHANGELOG** geführt; die `ArchitectureTests`-Suite prüft
> Engineering-Regeln (Layering, Dateigröße, kein stiller Catch), **nicht** die API-Oberfläche.
> Für Käufer relevant: SemVer-Zusagen beruhen aktuell auf Prozess/Review, nicht auf einem
> automatischen Diff-Gate. Der Aufbau eines echten API-Surface-Gates ist offene Folgearbeit.

---

## 4. Belegt vs. Vision — Übersicht

| Aussage | Status | Nachweis |
|---------|--------|----------|
| SDK-Lizenz = Apache-2.0 | **belegt** | `LICENSE`, `src/Directory.Build.props` |
| Verteilung als NuGet-Pakete inkl. `.snupkg`-Symbolen | **belegt** | `src/Directory.Build.props` |
| SemVer-Policy, aktuell `4.6.0-preview.2` | **belegt** | `docs/reference/semver-policy.md`, `src/Directory.Build.props` |
| Versionierungs-/Deprecation-Governance | **belegt (mit Caveat)** | ADR-006 (Surface-Gate nicht implementiert) |
| Modul-Registry (`IVoipClientModule`/`ModuleRegistry`) im SDK | **belegt (real)** | `src/Client/Application/...`, `ModuleRegistryTests.cs` |
| Host-zentrierter Plattform-Split (Engine OSS + Host + Plugins) | **Konzept (Accepted ADR)** | ADR-007 |
| Community Module Store, signierte Lizenzen, Stripe-Connect | **Konzept / Cross-Repo, Platzhalter/Demo** | ADR-008 (Proposed) |
| Lizenzstufen Developer/Commercial/OEM + Zusatzangebote | **Richtung / Vision** | interne CEO-Vision (auf Anfrage/NDA) |

---

## 5. Offene Punkte / Unsicherheiten

- **Store-Backend und Lizenz-Signierung liegen außerhalb dieses Repos.** Ihr tatsächlicher
  Reifegrad kann aus diesem SDK-Tree nicht abschließend belegt werden; die verfügbaren Quellen
  (ADR-008, interne Notizen) stufen sie als **Konzept/Demo** ein.
- **Kein aktives Preismodell:** Die Lizenzstufen sind Richtung, keine Zusage.
- **API-Surface-Gate fehlt** (ADR-006 Errata) — SemVer-Compliance ist heute prozessgetragen.
- **Trust-Boundary für Dritt-Module ist ungelöst** (ADR-008 §„Two shaping decisions"):
  .NET-Module laufen in-process mit vollem Zugriff; es gibt keinen echten In-Process-Sandbox.
  Die Aufnahme ungeprüften Dritt-Codes ist eine bewusste, noch offene Risiko-Entscheidung.

---

## Querverweise

- Versionierung / API-Kompatibilität: `../../adr/ADR-006-api-versioning-strategy.md`
- Plattform-Split (Engine/Host/Plugins): `../../adr/ADR-007-host-centric-platform-split.md`
- Community Module Store: `../../adr/ADR-008-community-module-store.md`
- SemVer-Policy: `../../reference/semver-policy.md`
