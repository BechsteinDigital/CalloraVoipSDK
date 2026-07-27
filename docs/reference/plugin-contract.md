# Plugin Contract v1 (Host-Centric)

Stand: 2026-04-16

## 1. Lifecycle

Pflichtoperationen im Host:

1. `install`
2. `activate`
3. `deactivate`
4. `uninstall`

State-Modell:

- `Installed`
- `Active`
- `Inactive`

## 2. Runtime Contracts

Ein Plugin muss eine Runtime Entry-Klasse bereitstellen:

- `IHostManagedPlugin` (`VoipHost.PluginContracts`)
- `ICalloraRuntimePlugin`

Host stellt:

- `IHostPluginLifecycle` (`VoipHost.PluginContracts`)
- `ICalloraPluginRuntime`
- `ICalloraPluginCatalog`
- `ICalloraPluginContext`

Hinweis:

- `ICalloraPluginCatalog` unterstuetzt Mehrfach-Exports je Vertragstyp.

## 3. API/Version Gates

1. Plugin muss mit Host-Contracts major-kompatibel sein.
2. Inkompatible Plugins duerfen nicht installiert/aktiviert werden.

## 4. Security & Trust (MVP + Zielbild)

MVP:

1. Whitelist-Pfade/Deployment-Policy
2. Kompatibilitaetspruefung bei Installation
3. Audit-Events fuer Lifecycle-Operationen

Zielbild:

1. Signierte Plugin-Pakete
2. Vertrauenskette / Zertifikatspruefung
3. zentrale Plugin-Katalog-API

## 5. UI Extension Surfaces

Begriffe:

- `Admin UI`: Betreiber/Backoffice
- `Workspace UI`: Agenten-/Endnutzeroberflaeche

Ein Plugin kann optional UI-Bausteine registrieren fuer:

1. Admin Navigation + Seiten
2. Workspace Navigation + Seiten
3. Dashboard Widgets
4. Aktionen/Buttons an definierten Extension Points

## 6. Compliance Metadata (Pflicht)

Plugin Manifest muss mindestens enthalten:

1. Datenkategorien
2. Verarbeitungszwecke
3. AI-Nutzung + Risikoklasse
4. benoetigte Berechtigungen
5. Retention-/Delete-Hinweise
