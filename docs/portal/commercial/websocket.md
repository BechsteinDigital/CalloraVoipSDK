# Callora.WebSocket

> **Status: in development — not yet available.** This page describes intent, not a
> shipping product.

**Callora.WebSocket** is the planned module for streaming raw call audio over a WebSocket
— for custom backends, external ASR/TTS, or your own realtime pipeline when you don't want
a vendor-specific realtime module.

## Planned capabilities

- Bidirectional PCM streaming of call audio over WebSocket.
- Framing/pacing suitable for external transcription or synthesis services.
- A transport primitive other modules (or your own code) can build on.

## Intended integration

Consumes the public [media tap](../guides/media-tap.md) and registers via the
[module registry](../concepts/modules.md) — the same contract available to your in-house
modules.

## Early access

[info@bechstein.digital](mailto:info@bechstein.digital)
