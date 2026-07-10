# Callora.Privacy

> **Status: in development — not yet available.** This page describes intent, not a
> shipping product.

**Callora.Privacy** is the planned compliance layer: redaction, consent management, policy
gates and an audit trail for call handling.

## Planned capabilities

- **Consent management** — capture and enforce recording/processing consent per call.
- **Redaction** — suppress or mask sensitive segments in recordings/transcripts.
- **Policy gates** — allow/deny processing steps based on jurisdiction or configuration.
- **Audit trail** — a tamper-evident record of what was captured, processed and by whom.

## Intended integration

A module over the [media tap](../guides/media-tap.md) and
[registry](../concepts/modules.md), positioned to gate other modules (e.g. block
transcription until consent is recorded).

## Early access

[info@bechstein.digital](mailto:info@bechstein.digital)
