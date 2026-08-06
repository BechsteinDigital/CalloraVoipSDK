# ADR-065: Public PCM Transcoding Surface

Status: Accepted
Date: 2026-08-06

## Context

A server-side consumer often needs to move audio between codecs through linear PCM. The prototypical case is an
SFU/mixer that decodes N−1 conference legs to PCM to mix a **phone** participant (G.711/G.722 over SIP) into a
**WebRTC** conference (Opus), then re-encodes the mix per leg. The SDK already wraps Concentus (Opus) and NAudio
(G.711 A-law/µ-law, G.722) internally for its own media path, but exposed no way for a consumer to reuse that
transcoding — forcing them to take a direct dependency on Concentus/NAudio and re-implement the framing.

We want to expose transcoding **without** leaking the third-party codec libraries into the public API (so the
SDK keeps ownership of those dependencies and can swap them), and without shipping a new NuGet package the
consumer must reference explicitly.

## Decision

Expose a small, transport-only transcoding surface in **`CalloraVoipSdk.Audio.Abstractions`**:

1. **`IAudioPayloadCodec`** — decode a codec payload to PCM16 and encode PCM16 to a codec payload. I/O is
   **PCM16 little-endian `byte[]`**; the interface carries the codec and the PCM sample rate.
2. **`AudioPayloadCodecFactory`** — creates the codec for a given active codec (Opus / G.711 A-law / G.711 µ-law
   / G.722), optionally at a specified PCM sample rate.
3. **Concentus/NAudio never appear in a public signature** — the concrete wrappers adapt them behind the
   interface.
4. **Fail-closed rate validation:** fixed-rate codecs (G.711/G.722) reject a non-canonical `pcmSampleRate`; Opus
   accepts 8/12/16/24/48 kHz; an invalid Opus frame length throws a named `ArgumentException`.
5. **Stateful contract:** one instance per stream direction (Opus and G.722 carry inter-frame state); this is
   documented on the interface.
6. **Shipped transitively** via the `CalloraVoipSdk` meta-package — no new `PackageReference` for the consumer.
   The `Audio.*` assemblies sit **outside** the Client/Core `PublicApi.approved.txt` baseline gate by design, so
   this surface is governed by its own module contract rather than the SDK-facade baseline.

## Consequences

- **Additive, dependency-clean:** a consumer transcodes Opus/G.711/G.722 ↔ PCM16 without referencing Concentus
  or NAudio, and the SDK keeps those dependencies internal and swappable.
- **The SFU mixing use case is unblocked** using the same codecs the media path already uses (bit-parity).
- **The statefulness contract is explicit**, so a consumer that reuses one instance across both directions (a
  correctness bug for Opus/G.722) is warned in the docs.
- **No native encode/decode is added** beyond the existing wrappers — this is a transport/transcoding surface,
  consistent with the SDK's "codec transport-only, no native codec engine" posture.

## Alternatives considered

- **Expose Concentus/NAudio directly.** Rejected: it leaks third-party types into the public API, couples the
  consumer to the implementation, and blocks swapping the codec backend.
- **No public surface (keep transcoding internal).** Rejected: it forces every server-side consumer to take the
  codec dependencies and re-implement framing/rate handling that the SDK already gets right.
- **A separate opt-in NuGet package.** Rejected for now: transitive delivery via the meta-package is simpler for
  the consumer and the surface is small; a split can follow if the dependency footprint becomes a concern.
