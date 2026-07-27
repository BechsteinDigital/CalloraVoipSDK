# ADR-007: Host-Centric Platform Split (Engine OSS + Host + Plugins)

Status: Accepted
Date: 2026-04-16

## Context

CalloraVoipSdk soll sich von einem reinen SDK zu einer erweiterbaren Voice-Plattform entwickeln:

- Engine als offenes, schlankes Telekommunikationsfundament (SIP/RTP/RTCP/SRTP/Media)
- Host als Produkt- und SaaS-Schicht (Tenants, User, Trunks, API, Entitlements, Plugin Lifecycle)
- Plugins als Feature-Schicht (Dialer, Contact Center, Realtime AI, Risk, Policy, Privacy)

Wichtiger Begriffsschnitt:

- `Admin UI`: Betreiber-/Backoffice-UI
- `Workspace UI`: Endnutzer-/Agent-UI

Der Begriff `Storefront` wird fuer CalloraVoipSdk nicht verwendet.

## Decision

Wir schneiden die Plattform host-zentriert:

1. `voipsdk-engine` (OSS):
   - Telephony Engine + minimal stabile Abstractions
   - keine Tenant-, User-, Billing-, Plugin-Lifecycle-Orchestrierung
2. `voipsdk-host`:
   - Plugin Runtime/Registry/Lifecycle (`install/activate/deactivate/uninstall`)
   - Control Plane APIs (Tenants, Users, Trunks, Entitlements)
   - UI shells (`Admin UI`, `Workspace UI`) + Extension Registry
3. `voipsdk-plugins-*`:
   - Produktmodule gegen Host Contracts
   - optional UI-Erweiterungen fuer Admin/Workspace

## Consequences

Positive:

- Engine kann realistisch Open Source werden.
- Produktwert liegt klar im Host + Plugins.
- Cloud und Self-hosted nutzen denselben Plattformkern.

Tradeoffs:

- Plugins sind an Host Contracts gekoppelt.
- API-/Contract Governance wird zentral kritisch.

## Guardrails

- Engine referenziert keine Host-/Plugin-Implementierungen.
- Host kontrolliert Plugin-Sicherheit, Versionierung, Entitlements.
- Plugin Lifecycle ist host-seitig die einzige Write-Authority fuer Runtime-Zustaende.
