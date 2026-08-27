# SemVer Policy

Gültig für alle `CalloraVoipSdk.*` Pakete.

## Version-Schema

- `MAJOR.MINOR.PATCH`
- Aktuelle Release-Linie: `4.x` (aktuell `4.12.0`)

## Erhöhungsregeln

- `MAJOR`: Breaking Changes in Public API/Verhalten.
- `MINOR`: Rückwärtskompatible Features/Erweiterungen.
- `PATCH`: Bugfixes, Performance- und Security-Fixes ohne API-Break.

## Release-Kanäle

- Stable: `x.y.z` (aktueller Kanal)
- Preview: `x.y.z-preview.n` (nur für Vorabstände zwischen Releases)
- RC: `x.y.z-rc.n`

## Paketversionen

- Alle `CalloraVoipSdk.*` Kernpakete werden pro Release auf dieselbe Version gesetzt.
- Symbole werden als `.snupkg` mit derselben Version ausgeliefert.
