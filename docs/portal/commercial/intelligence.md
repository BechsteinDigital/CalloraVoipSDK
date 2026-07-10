# Callora.Intelligence

> **Status: in development — not yet available.** This page describes intent, not a
> shipping product.

**Callora.Intelligence** is the planned analytics layer: answering-machine detection
(AMD), sentiment, transcription and local model integration.

## Planned capabilities

- **AMD** — distinguish a live human from an answering machine early in the call.
- **Transcription** — speech-to-text over the call audio, streaming or batched.
- **Sentiment** — derive sentiment/quality signals from the conversation.
- **Local models** — run supported models on-prem for data-residency-sensitive
  deployments.

## Intended integration

A module over the [media tap](../guides/media-tap.md) and
[registry](../concepts/modules.md); composes with [Privacy](privacy.md) for
consent-gated processing and can feed [Risk](risk.md).

## Early access

[info@bechstein.digital](mailto:info@bechstein.digital)
