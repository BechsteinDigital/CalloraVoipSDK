# ADR-071: What the Clear-Media Video Path Reads, and Which of It Survives Partial Encryption

Status: Accepted
Date: 2026-08-17

## Context

ADR-068 built the *opaque* video path (#223): two SDK endpoints carry ciphertext byte-identically without
reading the frame. The research behind it produced a second finding that the opaque path does not address —
**no shipping end-to-end-encryption system makes the payload opaque on the wire.** Every one of them leaves
the bytes a packetiser and a forwarder must read in the clear and encrypts the rest:

- Jitsi (`lib-jitsi-meet/modules/e2ee/Context.ts`): `UNENCRYPTED_BYTES = { delta: 3, key: 10, undefined: 1 }`
- libwebrtc's frame cryptor (LiveKit and others): per-codec "unencrypted header bytes"; for H.264 the clear
  prefix must cover the NAL headers, and the ciphertext is RBSP-escaped so it cannot emulate a start code
- RFC 9605 §6.1/§6.2: the forwarder works from RTP metadata; key-frame requests sit outside the encryption
  layer

A browser using Encoded Transform therefore lands on the SDK's **clear-media** path with a payload that is
readable only in its first bytes — and that path kept its payload assumptions, deliberately, because they are
correct for genuine clear media.

This ADR records what those assumptions actually are. Point-by-point, not in the abstract: the reason #310
exists is that "the clear path reads the payload" was a statement nobody had made precise.

## The inventory

### VP8 (`Vp8Depacketiser`, `Vp8PayloadDescriptor`)

| Read | Where it comes from | Survives partial encryption? |
|---|---|---|
| RTP payload descriptor: S bit, PID, and the optional I/L/T/K extension bytes (RFC 7741 §4.2) | Written by the *sender's packetiser*, after any frame transform | **Yes** — structurally. It is not part of the encoded frame. |
| One frame byte: `payload[headerLength] & 0x01`, the P bit (RFC 7741 §4.3 → RFC 6386 §9.1) | The first byte of the encoded frame | **Not guaranteed.** Clear for Jitsi (3/10 bytes) and for libwebrtc's frame cryptor; ciphertext for a sender that encrypts the frame whole, and then the key-frame flag is a coin toss. |

Nothing else is read. The remainder is copied through verbatim.

### H.264 (`H264Depacketiser`)

| Read | Where it comes from | Survives partial encryption? |
|---|---|---|
| `payload[0] & 0x1F` — NAL type dispatch (RFC 6184 §5.2) | RTP-level NAL header, written by the packetiser | **Yes** |
| STAP-A: 16-bit size fields per aggregated unit (§5.7.1) | Written by the packetiser when it aggregates | **Yes** — the aggregation is built after the transform, not encrypted with the frame |
| STAP-A: first byte of each aggregated unit, for IDR detection | NAL header of the aggregated NAL unit | **Yes** in practice — this is exactly the "unencrypted header bytes" every shipping implementation keeps clear |
| FU-A: indicator byte + FU header (§5.8), and the NAL header reconstructed from them | Written by the packetiser when it fragments | **Yes** |

The concern raised in #310 — that the STAP-A length fields sit "in the payload behind" the NAL header — does
not hold: those fields are produced by the sending packetiser, which runs *after* the encryption transform.
**H.264 key-frame detection (IDR, NAL type 5) is therefore structurally sound even for an encrypted stream.**
VP8's is the one genuine exception.

## Decision

1. **The key-frame flag carries its provenance to the SDK surface.** `EncodedFrame.KeyFrameSource` is
   `RtpHeaderExtension` (the Dependency Descriptor, #225 — written before encryption, always trustworthy),
   `Payload` (derived by the depacketiser, trustworthy as far as the table above allows), or `Unknown` (no
   signal at all). `IsKeyFrame` keeps its meaning and its type; nothing about an existing consumer changes.

2. **A payload format that does not read the payload says so, rather than reporting `false`.** The opaque
   depacketisers report `Unknown`, which is what their `isKeyFrame: false` always meant. This closes a
   documentation lie: with the descriptor negotiated, an opaque session now reports a *real* key-frame flag,
   so "always false under the switch" had stopped being true.

3. **No behavioural change to the clear path.** The VP8 P bit is still read and still believed for a session
   in the clear. Suppressing it for encrypted senders is not possible without knowing that the sender
   encrypts — which SDP does not say — and guessing would break genuine clear media, which is the common
   case.

4. **The remedy is the descriptor, not a heuristic.** For a peer that encrypts frames, the Dependency
   Descriptor is the answer, and the SDK offers it by default. This is also what the reference stacks do:
   libwebrtc treats the descriptor as the codec-agnostic carrier precisely because it is readable under
   E2EE; mediasoup moved to it after frame marking was removed; LiveKit states outright that payload-header
   parsing breaks under generic payload encryption.

## Consequences

- A forwarder built on the SDK can implement "trust only header-derived key frames" — no reference stack
  exposes enough to do that today; they report a merged boolean.
- The claim "with `OpaqueVideoFrames` on, `IsKeyFrame` is always false" is retired from the changelog. It was
  true before #225 and misleading after it.
- What remains open is the interop half of #310: H.264 receive from a browser using Encoded Transform, and a
  gate test that exercises it. This ADR deliberately does not predict that result — the last two assumptions
  about browser behaviour in this area (#261, and Chromium's descriptor on VP8 in #225) both came out against
  the expectation when finally measured.
