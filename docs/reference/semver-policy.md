# SemVer Policy

Gültig für alle `CalloraVoipSdk.*` Pakete.

## Version-Schema

- `MAJOR.MINOR.PATCH`
- Vor erstem stabilem Release: `0.x.y` (aktuell `4.6.0-preview.2`)
- Erstes stabiles Public Release: `1.0.0`

## Erhöhungsregeln

- `MAJOR`: Breaking Changes in Public API/Verhalten.
- `MINOR`: Rückwärtskompatible Features/Erweiterungen.
- `PATCH`: Bugfixes, Performance- und Security-Fixes ohne API-Break.

## Release-Kanäle

- Stable: `x.y.z`
- Preview: `x.y.z-preview.n`
- RC: `x.y.z-rc.n`

## Paketversionen

- Alle `CalloraVoipSdk.*` Kernpakete werden pro Release auf dieselbe Version gesetzt.
- Symbole werden als `.snupkg` mit derselben Version ausgeliefert.
