# Callora.Realtime

> **Status: in development — not yet available.** This page describes intent, not a
> shipping product.

**Callora.Realtime** is the planned bridge from a live call to a realtime AI voice API
(such as OpenAI Realtime) — the foundation for AI voice agents on top of the SDK.

## Planned capabilities

- Stream call audio to a realtime model and play its response back into the call.
- **Pacing and backpressure** so the model's output matches real-time playout without
  drift or overrun.
- **Barge-in** — cut the model's speech when the caller starts talking.

## Intended integration

Built on the public [media tap](../guides/media-tap.md): `CreateReceiver()` feeds caller
audio to the model, `CreateSender()` plays the model's audio back — registered through the
[module registry](../concepts/modules.md). No private SDK hooks.

## Early access

[info@bechstein.digital](mailto:info@bechstein.digital)
