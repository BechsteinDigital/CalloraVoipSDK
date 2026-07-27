# ADR-044: Video Packetisation and Contiguous-Release Reorder Playout

Status: Accepted
Date: 2026-07-14

## Context

To carry encoded video over RTP the SDK needs the codec payload formats (H.264 RFC 6184,
VP8 RFC 7741) and a receive-side reorder/playout window so out-of-order or RTX-recovered
packets reach the depacketiser in ascending sequence order. Both are pure protocol mechanics,
independent of security, feedback, or ICE. Codec is **transport-only** — the packetiser splits
an already-encoded frame and the depacketiser reassembles it; no native encode/decode.

### Verified current state (graphify-grounded)

- `src/Core/Infrastructure/Rtp/Packetisation/` holds the format layer: `IVideoPacketiser`
  (stateless, one instance serves any stream — its doc, L8) / `IVideoDepacketiser`
  (stateful per stream), `VideoRtpPayload` (readonly record struct, hot path), `AnnexBParser`,
  `H264Packetiser`/`H264Depacketiser`, `Vp8Packetiser`/`Vp8Depacketiser`, `VideoPayloadFormat`.
- Frame boundaries are detected by **both** the marker bit and an RTP-timestamp change:
  `H264Depacketiser.TryProcess` (L30-41) resets the frame under assembly when
  `rtpTimestamp != _timestamp` even without a closing marker — markerless senders exist.
  Fail-closed on malformed / unsupported modes (STAP-B, MTAP, FU-B) → discard the frame, never a
  corrupted access unit (class doc L5-11). H.264 IDR (NAL type 5) marks the key frame (L17-18).
- `VideoReorderBuffer` (`src/Core/Infrastructure/Rtp/VideoReorderBuffer.cs`) is
  **contiguous-release with gap-hold**: an in-order packet is emitted immediately (zero added
  latency), a missing next-expected sequence holds subsequent packets, and the hold is bounded to
  `depth` packets — beyond that `DrainReleasable` (L139-166) skips to the lowest buffered key and
  resumes. Extended sequence numbers (`ExtendSequenceNumber`, L182-201, RFC 3550 §A.1 signed
  16-bit delta) make it wrap-aware; duplicates and too-late packets are dropped. Thread-safe
  under `_sync`. Constructor bounds `depth ∈ [1, 16384]` (below 32768 so wrap cannot alias).
- Wiring: `VideoRtpStream` creates the buffer at `ReorderWindowDepth = 32` **only on
  RTX-negotiated legs** (L43, L186); without RTX the receive path is byte-for-byte the pre-RTX
  arrival-order passthrough (`Enqueue`, L440-450). `DeliverOrdered` (L455-473) resets the
  depacketiser on a release-order discontinuity — the gap the window could not fill.

## Decision

1. **Own the RTP payload formats** as a self-contained module: packetiser stateless, depacketiser
   stateful-per-stream (single-consumer, documented not thread-safe), frame boundary on marker
   **or** timestamp change, fail-closed discard on anything malformed or unsupported.
2. **Reorder = contiguous release with bounded gap-hold**, not a fixed-depth delay line. In-order
   traffic incurs no buffering latency; latency is only paid while a gap is open, capped by
   `depth`. A jump in the emitted sequence numbers is the consumer's concealment/keyframe cue.
3. **Reorder is a pure mechanic** — it never inspects payloads and makes no timing decisions; the
   hold is bounded by packet count, not a wall clock.
4. **Reorder is active only on RTX legs.** Without RTX the receive path stays the exact pre-RTX
   arrival-order passthrough — the buffer's cost (constant recovery-window latency) is only borne
   by legs that opted into the RTX trade-off.

### Crux

The reorder window doubles as the RTX-recovery window: `depth` bounds *both* how much reordering
is absorbed *and* how long a retransmitted packet has to arrive and slot into its gap before
playout. `depth = 32` balances a ~1-RTT retransmit window against added latency. Delivering
in-order immediately (rather than always holding `depth` packets) is what removes steady-state
latency — the original design held a constant window; contiguous-release replaced it.

## Consequences

Positive: in-order video has no reorder latency; RTX recovery and reordering share one bounded
mechanism; the format layer is isolated and unit-testable against hand-built libwebrtc-shaped
payloads.

Divergence / honesty:
- **No timeout-based playout, no RTCP-jitter coupling.** The hold is bounded by packet count
  (`depth`), not by a wall clock (`VideoReorderBuffer` doc L15; `VideoRtpStream` DECISION L38-43).
  Time-based playout is noted follow-up.
- A forward loss burst of ≥ half the sequence space is indistinguishable from a reorder under
  16-bit serial arithmetic (pathological, documented — see the loss-signalling ADR).
- VP8 is sent with a minimal descriptor (no PictureID); H.264 does not send STAP-A. These are
  noted follow-ups, not gaps that break interop with standard receivers.
- Roundtrip-tested against the SDK's own implementation and hand-built shapes; **no interop test
  against real encoders/browsers**.

## Guardrails

- Depacketiser resets the frame on marker **or** timestamp change; never merges half a frame into
  the next access unit.
- `VideoReorderBuffer` depth kept well below 32768 (wrap-alias safety, constructor-validated).
- Non-RTX receive path stays byte-identical to the pre-reorder passthrough (regression net).
- Reorder buffer stays payload-agnostic and timing-free (a mechanic, not a policy).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-video-packetisation.md`,
  `…-video-reorder-buffer.md`, `…-video-contiguous-playout.md`.
- Code (graphify-verified): `src/Core/Infrastructure/Rtp/Packetisation/` (`IVideoPacketiser`,
  `IVideoDepacketiser`, `H264Depacketiser.TryProcess` L30, `H264Packetiser`, `Vp8Packetiser`,
  `Vp8Depacketiser`, `AnnexBParser`, `VideoRtpPayload`, `VideoPayloadFormat`);
  `src/Core/Infrastructure/Rtp/VideoReorderBuffer.cs` (`Insert` L81, `DrainReleasable` L139,
  `ExtendSequenceNumber` L182); `src/Core/Infrastructure/Rtp/VideoRtpStream.cs`
  (`ReorderWindowDepth` L43, `Enqueue` L440, `DeliverOrdered` L455).
- Markers: `DECISION` in `VideoRtpStream` (`ReorderWindowDepth`).
- RFC: 6184 (H.264 RTP), 7741 (VP8 RTP), 3550 §A.1 (extended sequence numbers), 4588 (RTX window).
