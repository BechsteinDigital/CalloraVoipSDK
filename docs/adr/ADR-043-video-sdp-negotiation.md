# ADR-043: Video SDP Offer/Answer Negotiation (m=video, rtcp-fb, RTX/apt)

Status: Accepted
Date: 2026-07-14

## Context

The SDK gained a SIP-video signaling path: it must offer and answer an `m=video` media
description alongside audio, matching codecs, and negotiate the video-specific SDP attributes
that the media layer depends on — `a=rtcp-fb` (RFC 4585 §4.2) so both peers know which RTCP
feedback the other understands, and `a=rtx`/`apt` (RFC 4588 §8.1) so a repair stream can be
carried. This ADR covers the **SDP surface** for video; the media/security/feedback behaviour
those attributes gate is covered by the sibling C12 ADRs. Audio SDES offer/answer is ADR-C04-01;
BUNDLE SDP generation is the ADR-010/011 track (separate transport, not this SIP path).

The founding constraint (memory `project_video_interop_codec_decision`): **SIP-video first,
transport-only** — the SDK negotiates and moves codec bitstreams but does not natively
encode/decode. Codec support is therefore a capability list, not an encoder set.

### Verified current state (graphify-grounded)

- `VideoCodecCatalog` (`src/Core/Infrastructure/Sdp/VideoCodecCatalog.cs`) is the single source
  of truth: VP8 (PT 96) and H.264 (PT 97), both at the mandatory 90 kHz clock (`Defaults`,
  L13-17). H.264 offers `packetization-mode=1` (`BuildFmtp`, L59-63) and is only *accepted* when
  the peer explicitly declares mode 1 (`HasPacketizationMode1`, L49-52) — the packetiser always
  emits FU-A, which a mode-0 peer cannot receive.
- `SdpOfferAnswerNegotiator.TryNegotiateVideoAnswerMedia`
  (`src/Core/Infrastructure/Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs` L472) answers the first
  offered video m-line only; `SelectVideoCodecs` (L631) matches by **name + clock rate, never the
  static-PT fallback** the audio path uses (dynamic video PTs make a bare-PT match unsafe), and
  mirrors offered PTs per RFC 3264 §6.1.
- `NegotiateFeedback` (L87-92) answers the **intersection** of offered and implemented feedback
  (`nack`, `nack pli`, `ccm fir`); `NegotiateRtx` (L124-140) echoes only repair codecs whose
  `apt` points at an accepted PT, preserving the offered rtx numbering.
- Multi-m-line answers: every offered m-line is answered in offer order; unanswerable ones get
  port 0 (RFC 3264 §6). Only the first video m-line is negotiated, the rest port 0.

## Decision

Model video as a first-class SDP media section built from a static capability catalog, and make
the answer path **fail-closed and spec-exact**:

1. **`VideoCodecCatalog` is the only capability authority.** Offer emits VP8+H.264 (or the
   configured `PreferredVideoCodecs` subset) with `packetization-mode=1` on H.264; answer
   intersects offered against catalog by name+clock. No static-PT fallback for video.
2. **`a=rtcp-fb` is negotiated as a set intersection** (RFC 4585 §4.2), advertised for all
   formats (`*`). The offer advertises the full `StandardFeedback` set; the answer keeps only
   mutually-supported entries. NACK is advertised even though the retransmit path landed later —
   for symmetry with RTX-capable peers.
3. **`a=rtx`/`apt` is negotiated per accepted codec** (RFC 4588 §8.1): offer builds one repair
   codec per video codec with PTs above the highest video PT; answer echoes the peer's rtx PT/apt
   for accepted codecs only. `CallVideoParameters.RtxPayloadType` surfaces the result to the
   media layer.
4. **RFC 3264 §6 multi-m-line fidelity.** Every offered m-line is answered in order; a peer's
   video offer we cannot satisfy yields a zero-port line rather than a vanished m-line.

### Crux

Video PTs are dynamic, so the answer must match on `(name, clock)` and **must not** reuse the
audio path's static-PT fallback — a bare-PT match would answer a codec the peer never offered
(the M1 finding in the video-sdp log). H.264 acceptance is additionally gated on
`packetization-mode=1` because the packetiser is FU-A-only. These two gates are what keep the
answer honest about what the media layer can actually receive.

## Consequences

Positive: video negotiation reuses the audio offer/answer machinery and the catalog keeps codec
truth in one place. The RFC 3264 §6 fix (answer every m-line) benefits all inbound multi-m-line
offers, not just video.

Divergence / honesty:
- The `a=rtcp-fb` answer normalises a PT-specific offer (`96 ccm fir`) to `* ccm fir`.
  `*` is a superset, interop-safe for single-codec video; **per-PT mirroring is deferred**
  (`NegotiateFeedback` DECISION comment, L82-85) and would be needed for multi-codec video.
- Only the **first** video m-line is negotiated; further video m-lines get port 0.
- Codec is **transport-only**: negotiation succeeds with no native encoder/decoder behind it. A
  successful `m=video` answer is not a claim of end-to-end video without an application-supplied
  codec.

## Guardrails

- `SelectVideoCodecs` must never fall back to static-PT matching (regression-tested against the
  VP9@96-vs-VP8 collision).
- H.264 accepted only with explicit `packetization-mode=1`.
- `NegotiateFeedback`/`NegotiateRtx` are intersections — never advertise or echo something the
  peer did not offer / a codec not accepted.
- No end-to-end / interop / DONE claim from SDP tests alone (transport-only, SDK↔SDK roundtrips).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-video-sdp.md`,
  `…-video-rtcp-fb-sdp.md`, `…-video-rtx-sdp.md`.
- Code (graphify-verified): `src/Core/Infrastructure/Sdp/VideoCodecCatalog.cs`
  (`Defaults`, `BuildFmtp`, `HasPacketizationMode1`, `StandardFeedback`, `NegotiateFeedback`,
  `BuildRtx`, `NegotiateRtx`, `TryFindRtxPayloadType`);
  `src/Core/Infrastructure/Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs`
  (`TryNegotiateVideoAnswerMedia` L472, `SelectVideoCodecs` L631);
  `src/Core/Infrastructure/Sdp/Models/SdpRtcpFeedback.cs`.
- Markers: `DECISION` in `NegotiateFeedback` (`*`-normalisation).
- RFC: 3264 §6/§6.1 (offer/answer, m-line mirroring), 4585 §4.2 (rtcp-fb), 4588 §8.1 (rtx/apt),
  6184 §8.1 (H.264 packetization-mode), 7741 (VP8), 5104 §4.3.1 (FIR/ccm).
