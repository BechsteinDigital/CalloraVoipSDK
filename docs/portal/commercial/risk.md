# Callora.Risk

> **Status: in development — not yet available.** This page describes intent, not a
> shipping product.

**Callora.Risk** is the planned screening layer: spam/scam signals, call risk scoring and
PBX abuse prevention.

## Planned capabilities

- **Risk screening** — score inbound/outbound calls for spam/scam indicators.
- **Abuse prevention** — detect and throttle patterns that indicate PBX/trunk abuse.
- **Signals** — expose risk signals your dialplan or application logic can act on
  (accept / challenge / reject).

## Intended integration

A module over the [registry](../concepts/modules.md), consuming call metadata and (where
relevant) the [media tap](../guides/media-tap.md), surfacing signals your call-control
logic can gate on.

## Early access

[info@bechstein.digital](mailto:info@bechstein.digital)
